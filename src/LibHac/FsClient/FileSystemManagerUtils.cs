﻿using System;
using System.Buffers;
using System.Collections.Generic;
using LibHac.Common;
using LibHac.Fs;
using LibHac.FsClient.Accessors;

namespace LibHac.FsClient
{
    public static class FileSystemManagerUtils
    {
        public static Result CopyDirectory(this FileSystemManager fs, string sourcePath, string destPath,
            CreateFileOptions options = CreateFileOptions.None, IProgressReport logger = null)
        {
            Result rc = fs.OpenDirectory(out DirectoryHandle sourceHandle, sourcePath, OpenDirectoryMode.All);
            if (rc.IsFailure()) return rc;

            using (sourceHandle)
            {
                foreach (DirectoryEntryEx entry in fs.EnumerateEntries(sourcePath, "*", SearchOptions.Default))
                {
                    string subSrcPath = PathTools.Normalize(PathTools.Combine(sourcePath, entry.Name));
                    string subDstPath = PathTools.Normalize(PathTools.Combine(destPath, entry.Name));

                    if (entry.Type == DirectoryEntryType.Directory)
                    {
                        fs.EnsureDirectoryExists(subDstPath);

                        rc = fs.CopyDirectory(subSrcPath, subDstPath, options, logger);
                        if (rc.IsFailure()) return rc;
                    }

                    if (entry.Type == DirectoryEntryType.File)
                    {
                        logger?.LogMessage(subSrcPath);
                        fs.CreateOrOverwriteFile(subDstPath, entry.Size, options);

                        rc = fs.CopyFile(subSrcPath, subDstPath, logger);
                        if (rc.IsFailure()) return rc;
                    }
                }
            }

            return Result.Success;
        }

        public static Result CopyFile(this FileSystemManager fs, string sourcePath, string destPath, IProgressReport logger = null)
        {
            Result rc = fs.OpenFile(out FileHandle sourceHandle, sourcePath, OpenMode.Read);
            if (rc.IsFailure()) return rc;

            using (sourceHandle)
            {
                rc = fs.OpenFile(out FileHandle destHandle, destPath, OpenMode.Write | OpenMode.AllowAppend);
                if (rc.IsFailure()) return rc;

                using (destHandle)
                {
                    const int maxBufferSize = 0x10000;

                    rc = fs.GetFileSize(out long fileSize, sourceHandle);
                    if (rc.IsFailure()) return rc;

                    int bufferSize = (int)Math.Min(maxBufferSize, fileSize);

                    logger?.SetTotal(fileSize);

                    byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
                    try
                    {
                        for (long offset = 0; offset < fileSize; offset += bufferSize)
                        {
                            int toRead = (int)Math.Min(fileSize - offset, bufferSize);
                            Span<byte> buf = buffer.AsSpan(0, toRead);

                            rc = fs.ReadFile(out long _, sourceHandle, offset, buf);
                            if (rc.IsFailure()) return rc;

                            rc = fs.WriteFile(destHandle, buf, offset);
                            if (rc.IsFailure()) return rc;

                            logger?.ReportAdd(toRead);
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                        logger?.SetTotal(0);
                    }

                    rc = fs.FlushFile(destHandle);
                    if (rc.IsFailure()) return rc;
                }
            }

            return Result.Success;
        }

        public static IEnumerable<DirectoryEntryEx> EnumerateEntries(this FileSystemManager fs, string path)
        {
            return fs.EnumerateEntries(path, "*");
        }

        public static IEnumerable<DirectoryEntryEx> EnumerateEntries(this FileSystemManager fs, string path, string searchPattern)
        {
            return fs.EnumerateEntries(path, searchPattern, SearchOptions.RecurseSubdirectories);
        }

        public static IEnumerable<DirectoryEntryEx> EnumerateEntries(this FileSystemManager fs, string path, string searchPattern, SearchOptions searchOptions)
        {
            bool ignoreCase = searchOptions.HasFlag(SearchOptions.CaseInsensitive);
            bool recurse = searchOptions.HasFlag(SearchOptions.RecurseSubdirectories);

            DirectoryEntry dirEntry = default;
            fs.OpenDirectory(out DirectoryHandle sourceHandle, path, OpenDirectoryMode.All).ThrowIfFailure();

            using (sourceHandle)
            {
                while (true)
                {
                    fs.ReadDirectory(out long entriesRead, SpanHelpers.AsSpan(ref dirEntry), sourceHandle);
                    if (entriesRead == 0) break;

                    DirectoryEntryEx entry = FileSystemExtensions.GetDirectoryEntryEx(ref dirEntry, path);

                    if (PathTools.MatchesPattern(searchPattern, entry.Name, ignoreCase))
                    {
                        yield return entry;
                    }

                    if (entry.Type != DirectoryEntryType.Directory || !recurse) continue;

                    IEnumerable<DirectoryEntryEx> subEntries =
                        fs.EnumerateEntries(PathTools.Combine(path, entry.Name), searchPattern, searchOptions);

                    foreach (DirectoryEntryEx subEntry in subEntries)
                    {
                        yield return subEntry;
                    }
                }
            }
        }

        public static bool DirectoryExists(this FileSystemManager fs, string path)
        {
            Result rc = fs.GetEntryType(out DirectoryEntryType type, path);

            return (rc.IsSuccess() && type == DirectoryEntryType.Directory);
        }

        public static bool FileExists(this FileSystemManager fs, string path)
        {
            Result rc = fs.GetEntryType(out DirectoryEntryType type, path);

            return (rc.IsSuccess() && type == DirectoryEntryType.File);
        }

        public static void EnsureDirectoryExists(this FileSystemManager fs, string path)
        {
            path = PathTools.Normalize(path);
            if (fs.DirectoryExists(path)) return;

            PathTools.GetMountNameLength(path, out int mountNameLength).ThrowIfFailure();

            // Find the first subdirectory in the path that doesn't exist
            int i;
            for (i = path.Length - 1; i > mountNameLength + 2; i--)
            {
                if (path[i] == '/')
                {
                    string subPath = path.Substring(0, i);

                    if (fs.DirectoryExists(subPath))
                    {
                        break;
                    }
                }
            }

            // path[i] will be a '/', so skip that character
            i++;

            // loop until `path.Length - 1` so CreateDirectory won't be called multiple
            // times on path if the last character in the path is a '/'
            for (; i < path.Length - 1; i++)
            {
                if (path[i] == '/')
                {
                    string subPath = path.Substring(0, i);

                    fs.CreateDirectory(subPath);
                }
            }

            fs.CreateDirectory(path);
        }

        public static void CreateOrOverwriteFile(this FileSystemManager fs, string path, long size)
        {
            fs.CreateOrOverwriteFile(path, size, CreateFileOptions.None);
        }

        public static void CreateOrOverwriteFile(this FileSystemManager fs, string path, long size, CreateFileOptions options)
        {
            path = PathTools.Normalize(path);

            if (fs.FileExists(path)) fs.DeleteFile(path);

            fs.CreateFile(path, size, CreateFileOptions.None);
        }

        internal static bool IsEnabledFileSystemAccessorAccessLog(this FileSystemManager fs, string mountName)
        {
            if (fs.MountTable.Find(mountName, out FileSystemAccessor accessor).IsFailure())
            {
                return true;
            }

            return accessor.IsAccessLogEnabled;
        }
    }
}
