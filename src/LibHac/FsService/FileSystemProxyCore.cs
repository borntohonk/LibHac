﻿using System;
using LibHac.Fs;
using LibHac.Fs.Save;
using LibHac.FsService.Creators;

namespace LibHac.FsService
{
    public class FileSystemProxyCore
    {
        private FileSystemCreators FsCreators { get; }
        private byte[] SdEncryptionSeed { get; } = new byte[0x10];

        private const string NintendoDirectoryName = "Nintendo";
        private const string ContentDirectoryName = "Contents";

        public FileSystemProxyCore(FileSystemCreators fsCreators)
        {
            FsCreators = fsCreators;
        }

        public Result OpenBisFileSystem(out IFileSystem fileSystem, string rootPath, BisPartitionId partitionId)
        {
            return FsCreators.BuiltInStorageFileSystemCreator.Create(out fileSystem, rootPath, partitionId);
        }

        public Result OpenSdCardFileSystem(out IFileSystem fileSystem)
        {
            return FsCreators.SdFileSystemCreator.Create(out fileSystem);
        }

        public Result OpenContentStorageFileSystem(out IFileSystem fileSystem, ContentStorageId storageId)
        {
            fileSystem = default;

            string contentDirPath = default;
            IFileSystem baseFileSystem = default;
            bool isEncrypted = false;
            Result rc;

            switch (storageId)
            {
                case ContentStorageId.System:
                    rc = OpenBisFileSystem(out baseFileSystem, string.Empty, BisPartitionId.System);
                    contentDirPath = $"/{ContentDirectoryName}";
                    break;
                case ContentStorageId.User:
                    rc = OpenBisFileSystem(out baseFileSystem, string.Empty, BisPartitionId.User);
                    contentDirPath = $"/{ContentDirectoryName}";
                    break;
                case ContentStorageId.SdCard:
                    rc = OpenSdCardFileSystem(out baseFileSystem);
                    contentDirPath = $"/{NintendoDirectoryName}/{ContentDirectoryName}";
                    isEncrypted = true;
                    break;
                default:
                    rc = ResultFs.InvalidArgument;
                    break;
            }

            if (rc.IsFailure()) return rc;

            baseFileSystem.EnsureDirectoryExists(contentDirPath);

            rc = FsCreators.SubDirectoryFileSystemCreator.Create(out IFileSystem subDirFileSystem,
                baseFileSystem, contentDirPath);
            if (rc.IsFailure()) return rc;

            if (!isEncrypted)
            {
                fileSystem = subDirFileSystem;
                return Result.Success;
            }

            return FsCreators.EncryptedFileSystemCreator.Create(out fileSystem, subDirFileSystem,
                EncryptedFsKeyId.Content, SdEncryptionSeed);
        }

        public Result OpenCustomStorageFileSystem(out IFileSystem fileSystem, CustomStorageId storageId)
        {
            fileSystem = default;

            switch (storageId)
            {
                case CustomStorageId.SdCard:
                {
                    Result rc = FsCreators.SdFileSystemCreator.Create(out IFileSystem sdFs);
                    if (rc.IsFailure()) return rc;

                    string customStorageDir = Util.GetCustomStorageDirectoryName(CustomStorageId.SdCard);
                    string subDirName = $"/{NintendoDirectoryName}/{customStorageDir}";

                    rc = Util.CreateSubFileSystem(out IFileSystem subFs, sdFs, subDirName, true);
                    if (rc.IsFailure()) return rc;

                    rc = FsCreators.EncryptedFileSystemCreator.Create(out IFileSystem encryptedFs, subFs,
                        EncryptedFsKeyId.CustomStorage, SdEncryptionSeed);
                    if (rc.IsFailure()) return rc;

                    fileSystem = encryptedFs;
                    return Result.Success;
                }
                case CustomStorageId.User:
                {
                    Result rc = FsCreators.BuiltInStorageFileSystemCreator.Create(out IFileSystem userFs, string.Empty,
                        BisPartitionId.User);
                    if (rc.IsFailure()) return rc;

                    string customStorageDir = Util.GetCustomStorageDirectoryName(CustomStorageId.User);
                    string subDirName = $"/{customStorageDir}";

                    rc = Util.CreateSubFileSystem(out IFileSystem subFs, userFs, subDirName, true);
                    if (rc.IsFailure()) return rc;

                    fileSystem = subFs;
                    return Result.Success;
                }
                default:
                    return ResultFs.InvalidArgument.Log();
            }
        }

        public Result SetSdCardEncryptionSeed(ReadOnlySpan<byte> seed)
        {
            seed.CopyTo(SdEncryptionSeed);
            //FsCreators.SaveDataFileSystemCreator.SetSdCardEncryptionSeed(seed);

            return Result.Success;
        }

        public bool AllowDirectorySaveData(SaveDataSpaceId spaceId, string saveDataRootPath)
        {
            return spaceId == SaveDataSpaceId.User && !string.IsNullOrWhiteSpace(saveDataRootPath);
        }

        public Result OpenSaveDataFileSystem(out IFileSystem fileSystem, SaveDataSpaceId spaceId, ulong saveDataId,
            string saveDataRootPath, bool openReadOnly, SaveDataType type, bool cacheExtraData)
        {
            fileSystem = default;

            Result rc = OpenSaveDataDirectory(out IFileSystem saveDirFs, spaceId, saveDataRootPath, true);
            if (rc.IsFailure()) return rc;

            bool allowDirectorySaveData = AllowDirectorySaveData(spaceId, saveDataRootPath);
            bool useDeviceUniqueMac = Util.UseDeviceUniqueSaveMac(spaceId);

            if (allowDirectorySaveData)
            {
                try
                {
                    saveDirFs.EnsureDirectoryExists(GetSaveDataIdPath(saveDataId));
                }
                catch (HorizonResultException ex)
                {
                    return ex.ResultValue;
                }
            }

            // Missing save FS cache lookup

            rc = FsCreators.SaveDataFileSystemCreator.Create(out IFileSystem saveFs,
                out ISaveDataExtraDataAccessor extraDataAccessor, saveDirFs, saveDataId, allowDirectorySaveData,
                useDeviceUniqueMac, type, null);

            if (rc.IsFailure()) return rc;

            if (cacheExtraData)
            {
                // Missing extra data caching
            }

            fileSystem = openReadOnly ? new ReadOnlyFileSystem(saveFs) : saveFs;

            return Result.Success;
        }

        public Result OpenSaveDataDirectory(out IFileSystem fileSystem, SaveDataSpaceId spaceId, string saveDataRootPath, bool openOnHostFs)
        {
            if (openOnHostFs && AllowDirectorySaveData(spaceId, saveDataRootPath))
            {
                Result rc = FsCreators.TargetManagerFileSystemCreator.Create(out IFileSystem hostFs, false);

                if (rc.IsFailure())
                {
                    fileSystem = default;
                    return rc;
                }

                return Util.CreateSubFileSystem(out fileSystem, hostFs, saveDataRootPath, true);
            }

            string dirName = spaceId == SaveDataSpaceId.TemporaryStorage ? "/temp" : "/save";

            return OpenSaveDataDirectoryImpl(out fileSystem, spaceId, dirName, true);
        }

        public Result OpenSaveDataDirectoryImpl(out IFileSystem fileSystem, SaveDataSpaceId spaceId, string saveDirName, bool createIfMissing)
        {
            fileSystem = default;
            Result rc;

            switch (spaceId)
            {
                case SaveDataSpaceId.System:
                    rc = OpenBisFileSystem(out IFileSystem sysFs, string.Empty, BisPartitionId.System);
                    if (rc.IsFailure()) return rc;

                    return Util.CreateSubFileSystem(out fileSystem, sysFs, saveDirName, createIfMissing);

                case SaveDataSpaceId.User:
                case SaveDataSpaceId.TemporaryStorage:
                    rc = OpenBisFileSystem(out IFileSystem userFs, string.Empty, BisPartitionId.User);
                    if (rc.IsFailure()) return rc;

                    return Util.CreateSubFileSystem(out fileSystem, userFs, saveDirName, createIfMissing);

                case SaveDataSpaceId.SdSystem:
                case SaveDataSpaceId.SdCache:
                    rc = OpenSdCardFileSystem(out IFileSystem sdFs);
                    if (rc.IsFailure()) return rc;

                    string sdSaveDirPath = $"/{NintendoDirectoryName}{saveDirName}";

                    rc = Util.CreateSubFileSystem(out IFileSystem sdSubFs, sdFs, sdSaveDirPath, createIfMissing);
                    if (rc.IsFailure()) return rc;

                    return FsCreators.EncryptedFileSystemCreator.Create(out fileSystem, sdSubFs,
                        EncryptedFsKeyId.Save, SdEncryptionSeed);

                case SaveDataSpaceId.ProperSystem:
                    rc = OpenBisFileSystem(out IFileSystem sysProperFs, string.Empty, BisPartitionId.SystemProperPartition);
                    if (rc.IsFailure()) return rc;

                    return Util.CreateSubFileSystem(out fileSystem, sysProperFs, saveDirName, createIfMissing);

                case SaveDataSpaceId.Safe:
                    rc = OpenBisFileSystem(out IFileSystem safeFs, string.Empty, BisPartitionId.SafeMode);
                    if (rc.IsFailure()) return rc;

                    return Util.CreateSubFileSystem(out fileSystem, safeFs, saveDirName, createIfMissing);

                default:
                    return ResultFs.InvalidArgument.Log();
            }
        }

        private string GetSaveDataIdPath(ulong saveDataId)
        {
            return $"/{saveDataId:x16}";
        }
    }
}
