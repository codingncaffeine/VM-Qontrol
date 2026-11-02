using VmQontrol.App.ViewModels;

namespace VmQontrol.Tests;

public sealed class ConsoleCleanerTests
{
    [Fact]
    public void An_escape_sequence_split_across_two_reads_never_shows()
    {
        var cleaner = new ConsoleViewModel.EscapeCleaner();
        var first = cleaner.Feed("Booting\r\n\u001b");
        var second = cleaner.Feed("[22;15H Alpine\u001b[0m ready\r\n");
        Assert.Equal("Booting\n Alpine ready\n", first + second);
    }

    [Fact]
    public void Colour_and_title_sequences_are_removed() =>
        Assert.Equal("login: ", new ConsoleViewModel.EscapeCleaner().Feed("\u001b[1;32mlogin: \u001b[0m\u001b]0;title\u0007"));

    /// <summary>The login program resets the terminal (ESC c) and switches character sets just before its prompt: the prompt must show at once.</summary>
    [Fact]
    public void A_prompt_after_a_reset_and_charset_switch_is_not_held_back()
    {
        var cleaner = new ConsoleViewModel.EscapeCleaner();
        Assert.Equal("\nlocalhost login: ", cleaner.Feed("\u001bc\u001b(B\u001b)0\u001b%@\r\nlocalhost login: "));
    }

    [Fact]
    public void Only_a_sequence_that_can_still_complete_waits()
    {
        var cleaner = new ConsoleViewModel.EscapeCleaner();
        Assert.Equal("ready ", cleaner.Feed("ready \u001b[0;3"));
        Assert.Equal("go", cleaner.Feed("7mgo"));
    }
}
