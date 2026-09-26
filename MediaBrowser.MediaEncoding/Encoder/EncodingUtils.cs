#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Jellyfin.Extensions;
using MediaBrowser.Model.MediaInfo;

namespace MediaBrowser.MediaEncoding.Encoder
{
    public static class EncodingUtils
    {
        /// <summary>Rebinds exactly one generated file-input argument to its pinned read address.</summary>
        public static string BindFileInput(string arguments, string logicalPath, string readPath)
        {
            var original = "-i " + GetInputArgument("file", logicalPath, MediaProtocol.File);
            var index = arguments.IndexOf(original, StringComparison.Ordinal);
            var end = index + original.Length;
            if (index < 0 || (index > 0 && !char.IsWhiteSpace(arguments[index - 1]))
                || (end < arguments.Length && !char.IsWhiteSpace(arguments[end]))
                || arguments.IndexOf(original, end, StringComparison.Ordinal) >= 0)
            {
                throw new InvalidDataException("The native command must contain exactly one selected local-file input.");
            }

            return string.Concat(arguments.AsSpan(0, index), "-i " + GetInputArgument("file", readPath, MediaProtocol.File), arguments.AsSpan(end));
        }

        public static string GetInputArgument(string inputPrefix, string inputFile, MediaProtocol protocol)
        {
            if (protocol != MediaProtocol.File)
            {
                return string.Format(CultureInfo.InvariantCulture, "\"{0}\"", inputFile);
            }

            return GetFileInputArgument(inputFile, inputPrefix);
        }

        public static string GetInputArgument(string inputPrefix, IReadOnlyList<string> inputFiles, MediaProtocol protocol)
        {
            if (protocol != MediaProtocol.File)
            {
                return string.Format(CultureInfo.InvariantCulture, "\"{0}\"", inputFiles[0]);
            }

            return GetConcatInputArgument(inputFiles, inputPrefix);
        }

        /// <summary>
        /// Gets the concat input argument.
        /// </summary>
        /// <param name="inputFiles">The input files.</param>
        /// <param name="inputPrefix">The input prefix.</param>
        /// <returns>System.String.</returns>
        private static string GetConcatInputArgument(IReadOnlyList<string> inputFiles, string inputPrefix)
        {
            // Get all streams
            // If there's more than one we'll need to use the concat command
            if (inputFiles.Count > 1)
            {
                var files = string.Join('|', inputFiles.Select(f => f.EscapeProcessArgument()));

                return string.Format(CultureInfo.InvariantCulture, "concat:\"{0}\"", files);
            }

            // Determine the input path for video files
            return GetFileInputArgument(inputFiles[0], inputPrefix);
        }

        /// <summary>
        /// Gets the file input argument.
        /// </summary>
        /// <param name="path">The path.</param>
        /// <param name="inputPrefix">The path prefix.</param>
        /// <returns>System.String.</returns>
        private static string GetFileInputArgument(string path, string inputPrefix)
        {
            if (path.Contains("://", StringComparison.Ordinal))
            {
                return string.Format(CultureInfo.InvariantCulture, "\"{0}\"", path);
            }

            path = path.EscapeProcessArgument();

            return string.Format(CultureInfo.InvariantCulture, "{1}:\"{0}\"", path, inputPrefix);
        }
    }
}
