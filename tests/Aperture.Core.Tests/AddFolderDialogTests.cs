using Aperture.App;
using Aperture.Core.Tests.Support;

namespace Aperture.Core.Tests;

/// <summary>The subfolder question, shown whenever folders are added: Yes unless the user says otherwise.</summary>
public class AddFolderDialogTests
{
    [Fact]
    public void IncludeSubfolders_IsTheDefault()
    {
        Sta.Run(() =>
        {
            var dialog = new AddFolderDialog([@"C:\Photos\Trips"]);

            Assert.True(dialog.IncludeSubfolders);
            Assert.Equal("Trips", dialog.FolderNameText.Text);
            Assert.Equal(@"C:\Photos\Trips", dialog.FolderPathText.Text);
            dialog.Close();
        });
    }

    [Fact]
    public void ForSeveralFolders_SaysHowMany_AndNamesThem()
    {
        Sta.Run(() =>
        {
            var dialog = new AddFolderDialog([@"C:\Photos\Trips", @"D:\Pets", @"E:\Work"]);

            Assert.True(dialog.IncludeSubfolders);
            Assert.Equal("3 folders", dialog.FolderNameText.Text);
            Assert.Equal("Trips, Pets, Work", dialog.FolderPathText.Text);
            dialog.Close();
        });
    }
}
