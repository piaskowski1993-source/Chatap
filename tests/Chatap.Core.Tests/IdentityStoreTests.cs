namespace Chatap.Core.Tests;

public class IdentityStoreTests
{
    static string NewTempFolder() => Path.Combine(Path.GetTempPath(), "chatap-test-" + Guid.NewGuid());

    [Fact]
    public void First_start_writes_identity_and_backup_into_the_folder()
    {
        string folder = NewTempFolder();

        Guid id = IdentityStore.LoadOrCreate(folder);

        Assert.Equal(id.ToString(), File.ReadAllText(Path.Combine(folder, "identity")));
        Assert.Equal(id.ToString(), File.ReadAllText(Path.Combine(folder, "identity.bak")));
    }

    [Fact]
    public void Second_start_gives_the_same_guid()
    {
        string folder = NewTempFolder();

        Guid first = IdentityStore.LoadOrCreate(folder);
        Guid second = IdentityStore.LoadOrCreate(folder);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Damaged_main_file_uses_the_backup_and_repairs_the_main_file()
    {
        string folder = NewTempFolder();
        Guid original = IdentityStore.LoadOrCreate(folder);
        File.WriteAllText(Path.Combine(folder, "identity"), "nonsense");

        Guid loaded = IdentityStore.LoadOrCreate(folder);

        Assert.Equal(original, loaded);
        Assert.Equal(original.ToString(), File.ReadAllText(Path.Combine(folder, "identity")));
    }

    [Fact]
    public void Both_files_damaged_stops_with_a_clear_message()
    {
        string folder = NewTempFolder();
        IdentityStore.LoadOrCreate(folder);
        File.WriteAllText(Path.Combine(folder, "identity"), "nonsense");
        File.WriteAllText(Path.Combine(folder, "identity.bak"), "nonsense");

        var error = Assert.Throws<InvalidDataException>(() => IdentityStore.LoadOrCreate(folder));

        Assert.Contains("Identity files are damaged", error.Message);
        Assert.Contains(folder, error.Message);
    }

    [Fact]
    public void Two_data_folders_give_two_different_identities()
    {
        Guid copy1 = IdentityStore.LoadOrCreate(NewTempFolder());
        Guid copy2 = IdentityStore.LoadOrCreate(NewTempFolder());

        Assert.NotEqual(copy1, copy2);
    }
}
