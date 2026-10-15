namespace EllediTransfer;

public static class RemotePath
{
    public static string Combine(string path1, string path2)
    {
        if (path2.StartsWith('/'))
        {
            return path2;
        }
        if (string.IsNullOrEmpty(path1))
        {
            return path2;
        }
        return path1.TrimEnd('/') + "/" + path2;
    }

    public static string GetFileName(string path)
    {
        var trimmed = path.Length > 1 ? path.TrimEnd('/') : path;
        var index = trimmed.LastIndexOf('/');
        return index < 0 ? trimmed : trimmed[(index + 1)..];
    }

    public static string GetDirectoryName(string path)
    {
        var trimmed = path.Length > 1 ? path.TrimEnd('/') : path;
        var index = trimmed.LastIndexOf('/');
        return index switch
        {
            < 0 => ".",
            0 => "/",
            _ => trimmed[..index],
        };
    }

    /// <summary>Resolves "." and ".." segments of an absolute path.</summary>
    public static string Normalize(string path)
    {
        var segments = new List<string>();
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }
            if (segment == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }
                continue;
            }
            segments.Add(segment);
        }
        return "/" + string.Join("/", segments);
    }

    public static string TranslateRemotePathToLocal(string remotePath, string remoteRoot, string localRoot)
    {
        var relative = remotePath[remoteRoot.TrimEnd('/').Length..].TrimStart('/');
        return Path.Combine(localRoot, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    public static string TranslateLocalPathToRemote(string localPath, string localRoot, string remoteRoot)
    {
        var relative = Path.GetRelativePath(localRoot, localPath);
        return relative == "." ? remoteRoot : Combine(remoteRoot, relative.Replace(Path.DirectorySeparatorChar, '/'));
    }
}
