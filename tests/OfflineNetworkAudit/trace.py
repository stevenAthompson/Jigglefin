"""Trace only a newly-created hidden test process and its gated descendants.

Usage: python trace.py --report <owned-fixture/report.json> -- <executable> <args>
Never attaches by name or to an existing user process. Uses retained Windows process
handles for cleanup, avoiding PID-reuse hazards. Instrumentation is not a product
dependency and does not block calls: attempted outbound calls fail the harness.
"""
import argparse
import concurrent.futures
import ctypes
from ctypes import wintypes
import hashlib
import json
import msvcrt
import os
from pathlib import Path
import subprocess
import sys
import threading
import time

import frida


class StartupInfo(ctypes.Structure):
    _fields_ = [("cb", wintypes.DWORD), ("reserved", wintypes.LPWSTR), ("desktop", wintypes.LPWSTR), ("title", wintypes.LPWSTR),
                ("x", wintypes.DWORD), ("y", wintypes.DWORD), ("xsize", wintypes.DWORD), ("ysize", wintypes.DWORD),
                ("xchars", wintypes.DWORD), ("ychars", wintypes.DWORD), ("fill", wintypes.DWORD), ("flags", wintypes.DWORD),
                ("show", wintypes.WORD), ("reserved_size", wintypes.WORD), ("reserved_data", ctypes.c_void_p),
                ("stdin", wintypes.HANDLE), ("stdout", wintypes.HANDLE), ("stderr", wintypes.HANDLE)]


class ProcessInfo(ctypes.Structure):
    _fields_ = [("process", wintypes.HANDLE), ("thread", wintypes.HANDLE), ("pid", wintypes.DWORD), ("tid", wintypes.DWORD)]


class JobBasicLimits(ctypes.Structure):
    _fields_ = [("process_time", ctypes.c_int64), ("job_time", ctypes.c_int64), ("flags", wintypes.DWORD),
                ("min_working_set", ctypes.c_size_t), ("max_working_set", ctypes.c_size_t), ("process_limit", wintypes.DWORD),
                ("affinity", ctypes.c_size_t), ("priority", wintypes.DWORD), ("scheduling", wintypes.DWORD)]


class JobLimits(ctypes.Structure):
    _fields_ = [("basic", JobBasicLimits), ("io_counters", ctypes.c_uint64 * 6), ("process_memory", ctypes.c_size_t),
                ("job_memory", ctypes.c_size_t), ("peak_process_memory", ctypes.c_size_t), ("peak_job_memory", ctypes.c_size_t)]


kernel = ctypes.WinDLL("kernel32", use_last_error=True)
kernel.CreateProcessW.argtypes = [wintypes.LPCWSTR, wintypes.LPWSTR, ctypes.c_void_p, ctypes.c_void_p, wintypes.BOOL,
                                wintypes.DWORD, ctypes.c_void_p, wintypes.LPCWSTR, ctypes.POINTER(StartupInfo), ctypes.POINTER(ProcessInfo)]
kernel.CreateProcessW.restype = wintypes.BOOL
kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
kernel.OpenProcess.restype = wintypes.HANDLE
for name in ["CloseHandle", "ResumeThread"]:
    getattr(kernel, name).argtypes = [wintypes.HANDLE]
kernel.ResumeThread.restype = wintypes.DWORD
kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
kernel.WaitForSingleObject.restype = wintypes.DWORD
kernel.GetExitCodeProcess.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD)]
kernel.TerminateProcess.argtypes = [wintypes.HANDLE, wintypes.UINT]
kernel.DebugActiveProcessStop.argtypes = [wintypes.DWORD]
kernel.CreateJobObjectW.argtypes = [ctypes.c_void_p, wintypes.LPCWSTR]
kernel.CreateJobObjectW.restype = wintypes.HANDLE
kernel.SetInformationJobObject.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD]
kernel.AssignProcessToJobObject.argtypes = [wintypes.HANDLE, wintypes.HANDLE]
kernel.PeekNamedPipe.argtypes = [wintypes.HANDLE, ctypes.c_void_p, wintypes.DWORD, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--report", required=True)
    parser.add_argument("--watch-stdin", action="store_true")
    parser.add_argument("command", nargs=argparse.REMAINDER)
    options = parser.parse_args()
    argv = options.command[1:] if options.command[:1] == ["--"] else options.command
    report_path = Path(options.report).resolve()
    if not any(parent.name.startswith(("jigglefin-folder-web-", "jigglefin-network-audit-")) for parent in report_path.parents):
        parser.error("Reports must stay inside an owned synthetic test fixture.")
    executable = Path(argv[0]).resolve(strict=True)
    script_source = Path(__file__).with_name("network-hooks.js").read_text(encoding="utf-8")
    report = {"fridaVersion": frida.__version__, "processes": [], "events": [], "errors": [], "forcedTerminations": []}
    lock = threading.RLock()
    sessions, scripts, handles = {}, {}, {}
    worker = concurrent.futures.ThreadPoolExecutor(max_workers=1)
    device = frida.get_local_device()

    def message(pid, message, data):
        with lock:
            if message["type"] == "send":
                payload = message["payload"]
                report["events"].append({"pid": pid, **payload})
                if payload["kind"] == "hook-error":
                    report["errors"].append(payload)
            else:
                report["errors"].append({"pid": pid, "message": message})

    def instrument(pid, binary, parent=None):
        session = device.attach(pid)
        with open(binary, "rb") as source:
            checksum = hashlib.file_digest(source, "sha256").hexdigest()
        with lock:
            sessions[pid] = session
            report["processes"].append({"pid": pid, "parent": parent, "executable": str(binary),
                                        "sha256": checksum})
        session.enable_child_gating()
        script = session.create_script(script_source)
        script.on("message", lambda msg, data: message(pid, msg, data))
        script.load()
        scripts[pid] = script

    def child_added(child):
        def attach_child():
            try:
                with lock:
                    if child.parent_pid not in sessions:
                        raise RuntimeError("Unexpected parent outside this test process tree")
                    handle = kernel.OpenProcess(0x100001, False, child.pid)  # SYNCHRONIZE | TERMINATE
                    if not handle:
                        raise ctypes.WinError(ctypes.get_last_error())
                    handles[child.pid] = handle
                instrument(child.pid, Path(child.path).resolve(strict=True), child.parent_pid)
                device.resume(child.pid)
            except Exception as error:
                with lock:
                    report["errors"].append({"child": child.pid, "error": str(error)})
                    if child.pid in handles:
                        kernel.TerminateProcess(handles[child.pid], 1)
        worker.submit(attach_child)

    device.on("child-added", child_added)
    info = ProcessInfo()
    startup = StartupInfo()
    startup.cb = ctypes.sizeof(startup)
    startup.flags = 0x101  # STARTF_USESTDHANDLES | STARTF_USESHOWWINDOW
    startup.show = 0       # SW_HIDE, in addition to CREATE_NO_WINDOW below
    startup.stdin = msvcrt.get_osfhandle(sys.stdin.fileno())
    startup.stdout = msvcrt.get_osfhandle(sys.stdout.fileno())
    startup.stderr = msvcrt.get_osfhandle(sys.stderr.fileno())
    command = ctypes.create_unicode_buffer(subprocess.list2cmdline([str(executable), *argv[1:]]))
    # Match Frida's suspended/debugged startup, additionally forbidding a console
    # window. No process instruction runs before the observers are installed.
    flags = 0x08000000 | 0x4 | 0x1 | 0x2  # NO_WINDOW | SUSPENDED | DEBUG_PROCESS | DEBUG_ONLY_THIS_PROCESS
    if not kernel.CreateProcessW(str(executable), command, None, None, True, flags, None, None, ctypes.byref(startup), ctypes.byref(info)):
        raise ctypes.WinError(ctypes.get_last_error())
    handles[info.pid] = info.process
    job = kernel.CreateJobObjectW(None, None)
    code = wintypes.DWORD(1)
    try:
        limits = JobLimits()
        limits.basic.flags = 0x2000  # JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        if not job or not kernel.SetInformationJobObject(job, 9, ctypes.byref(limits), ctypes.sizeof(limits)) or not kernel.AssignProcessToJobObject(job, info.process):
            raise ctypes.WinError(ctypes.get_last_error())
        if not kernel.DebugActiveProcessStop(info.pid):
            raise ctypes.WinError(ctypes.get_last_error())
        instrument(info.pid, executable)
        print("JIGGLEFIN_NATIVE_TRACE_ROOT:" + str(info.pid), flush=True)
        if kernel.ResumeThread(info.thread) == 0xffffffff:
            raise ctypes.WinError(ctypes.get_last_error())
        while kernel.WaitForSingleObject(info.process, 250) == 0x102:
            # Poll the controller's pipe without a daemon thread blocked in Python
            # buffered I/O during interpreter shutdown.
            if options.watch_stdin and not kernel.PeekNamedPipe(startup.stdin, None, 0, None, None, None):
                raise RuntimeError("The audit controller disconnected; stopping its owned process tree")
            with lock:
                if report["errors"]:
                    raise RuntimeError("Native observer failed; incomplete capture cannot pass")
        kernel.GetExitCodeProcess(info.process, ctypes.byref(code))
    except BaseException as error:
        report["errors"].append({"error": str(error)})
        code.value = 1
    finally:
        device.off("child-added", child_added)
        worker.shutdown(wait=True)
        # Kill only retained handles to this test's own still-live processes.
        for pid, handle in handles.items():
            if kernel.WaitForSingleObject(handle, 2000) == 0x102:
                report["forcedTerminations"].append(pid)
                kernel.TerminateProcess(handle, 1)
            kernel.CloseHandle(handle)
        kernel.CloseHandle(info.thread)
        if job:
            kernel.CloseHandle(job)
        time.sleep(0.1)  # Deliver final asynchronous observer messages.
        for session in sessions.values():
            if not session.is_detached:
                session.detach()
        # Finish native callback threads before Python finalizes their closures.
        # Otherwise a heavily gated process tree can crash during interpreter exit.
        frida.shutdown()
        scripts.clear()
        sessions.clear()
        report["exitCode"] = code.value
        report_path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    return 1 if report["errors"] or report["forcedTerminations"] else code.value


if __name__ == "__main__":
    raise SystemExit(main())
