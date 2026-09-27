using System;
using System.IO;

namespace MediaBrowser.Controller.Library;

/// <summary>Preserves work already spent when a bounded directory read fails partway through.</summary>
public sealed class LiveDirectoryReadException : IOException
{
    /// <summary>Initializes a new instance of the <see cref="LiveDirectoryReadException"/> class.</summary>
    /// <param name="entriesExamined">Raw entries yielded before the failure.</param>
    /// <param name="innerException">The filesystem failure.</param>
    public LiveDirectoryReadException(int entriesExamined, Exception innerException)
        : base("A bounded directory read failed before completion.", innerException)
    {
        EntriesExamined = entriesExamined;
    }

    /// <summary>Gets the entry budget already spent, even though no listing was returned.</summary>
    public int EntriesExamined { get; }
}
