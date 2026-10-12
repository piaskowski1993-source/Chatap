namespace Chatap.Core;

// Loads this node's permanent ID from <folder>/identity, with <folder>/identity.bak as a backup copy.
// First start: makes a new ID and writes both files.
public static class IdentityStore
{
    public static Guid LoadOrCreate(string folder)
    {
        Directory.CreateDirectory(folder);
        string main = Path.Combine(folder, "identity");
        string backup = Path.Combine(folder, "identity.bak");

        Guid? fromMain = TryRead(main);
        Guid? fromBackup = TryRead(backup);

        if (fromMain is Guid id)
        {
            if (fromBackup != id)
                WriteSafely(backup, id);
            return id;
        }

        if (fromBackup is Guid saved)
        {
            WriteSafely(main, saved);
            return saved;
        }

        if (!File.Exists(main) && !File.Exists(backup))
        {
            var created = Guid.NewGuid();
            WriteSafely(main, created);
            WriteSafely(backup, created);
            return created;
        }

        throw new InvalidDataException($"Identity files are damaged: {main}. Repair or delete them.");
    }

    static Guid? TryRead(string path) =>
        File.Exists(path) && Guid.TryParse(File.ReadAllText(path).Trim(), out var id) ? id : null;

    // Write to a temporary file, then rename it over the real file.
    // A crash during the write can only damage the temporary file.
    static void WriteSafely(string path, Guid id)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, id.ToString());
        File.Move(temp, path, overwrite: true);
    }
}
