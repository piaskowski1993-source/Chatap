namespace Chatap.Core.Tests;

public class DataFolderTests
{
    [Fact]
    public void Data_argument_selects_that_folder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "chatap-copy2");

        string result = DataFolder.FromArgs(["--data", folder]);

        Assert.Equal(folder, result);
    }

    [Fact]
    public void No_argument_uses_the_default_folder()
    {
        string result = DataFolder.FromArgs([]);

        Assert.Equal(DataFolder.Default, result);
        Assert.EndsWith("Chatap", result);
    }

    [Fact]
    public void Data_argument_without_a_folder_stops_with_a_message()
    {
        var error = Assert.Throws<ArgumentException>(() => DataFolder.FromArgs(["--data"]));

        Assert.Contains("--data needs a folder", error.Message);
    }
}
