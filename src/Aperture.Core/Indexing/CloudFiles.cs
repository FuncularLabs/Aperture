namespace Aperture.Core.Indexing;

/// <summary>
/// Cloud placeholders: files a sync client (OneDrive, Dropbox) keeps online only, which are downloaded when they're
/// read. Indexing reads each file, so these are the slow ones.
/// </summary>
public static class CloudFiles
{
    /// <summary>Whether a file with these attributes is online only (a placeholder that's downloaded when read).</summary>
    public static bool IsOnlineOnly(FileAttributes attributes) => (attributes & OnlineOnlyFlags) != 0;

    // FILE_ATTRIBUTE_OFFLINE (older sync clients), FILE_ATTRIBUTE_RECALL_ON_OPEN and FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS
    // (the Cloud Files API's placeholders: OneDrive, Dropbox). Pinned and unpinned only say what the user asked for.
    private const FileAttributes OnlineOnlyFlags = FileAttributes.Offline | (FileAttributes)0x40000 | (FileAttributes)0x400000;
}

/// <summary>How many supported files a scan has found so far, and how many of them are online only.</summary>
public readonly record struct ScanTally(int Found, int OnlineOnly);
