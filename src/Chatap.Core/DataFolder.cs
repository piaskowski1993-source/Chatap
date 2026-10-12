namespace Chatap.Core;

// Finds the data folder from the startup arguments: "--data <folder>", or the default per-user folder.
public static class DataFolder
{
    // ~/.local/share/Chatap on Linux.
    public static string Default =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Chatap");

    public static string FromArgs(string[] args)
    {
        int index = Array.IndexOf(args, "--data");
        if (index < 0)
            return Default;

        if (index + 1 >= args.Length)
            throw new ArgumentException("--data needs a folder, for example: --data ~/chatap-copy2");

        return Path.GetFullPath(ExpandHome(args[index + 1]));
    }

    // The shell expands ~ only when the path is not in quotes, so expand it here as well.
    static string ExpandHome(string path) =>
        path.StartsWith("~/")
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..])
            : path;
}
