using System.Windows.Threading;
using Aperture.App;
using Aperture.App.Services;
using Aperture.App.ViewModels;
using Aperture.Core.Indexing;
using Aperture.Core.Library;
using Aperture.Core.Tests.Support;

namespace Aperture.Core.Tests;

/// <summary>
/// The online-only question as the window asks it (0.9.2 C1, C5–C8): once per folder while the library is open, kept
/// when the user says so, chosen from the folder's menu, one dialog at a time, and given up when its folder or library
/// goes. A real view model on a real library; the dialog is the view model's seam. Each folder has here.jpg and
/// cloud.jpg, online only (the Offline attribute a placeholder carries).
/// </summary>
public class OnlineOnlyPromptTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private sealed class Fixture : IDisposable
    {
        public TempDir Data { get; } = new();
        public TempDir Photos { get; } = new();
        public LibraryService Library { get; private set; }
        public MainViewModel Vm { get; private set; }
        public List<OnlineOnlyQuestion> Asked { get; } = [];
        public Func<OnlineOnlyQuestion, OnlineOnlyAnswer?> Reply { get; set; } =
            _ => new OnlineOnlyAnswer(OnlineOnlyChoice.Skip, Remember: false);

        public Fixture()
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            foreach (var folder in new[] { "A", "B" })
            {
                TestImages.Write(Photos.Combine(folder, "here.jpg"), 32, 24);
                var cloud = Photos.Combine(folder, "cloud.jpg");
                TestImages.Write(cloud, 32, 24);
                File.SetAttributes(cloud, File.GetAttributes(cloud) | FileAttributes.Offline);
            }
            Library = new LibraryService(Data.Path);
            Vm = Open();
        }

        private MainViewModel Open()
        {
            var vm = new MainViewModel(Library, new ThumbnailService(Library))
            {
                AskIncludeSubfolders = _ => true,
                ShowOnlineOnlyDialog = q =>
                {
                    Asked.Add(q);
                    return Reply(q);
                },
            };
            Assert.True(Sta.PumpUntil(() => vm.LoadedItemCount is not null && !vm.IsLoadingView, Wait));
            return vm;
        }

        public string Folder(string name) => Photos.Combine(name);

        public RootVm Root(string alias) => Vm.Roots.Single(r => r.Alias == alias);

        public void Add(params string[] folders)
        {
            Vm.AddFolders(folders.Select(Folder));
            Idle();
        }

        public void Idle() => Assert.True(Sta.PumpUntil(() => !Vm.IsIndexing, Wait), "still indexing");

        /// <summary>Another run of the folder, as Resume starts one.</summary>
        public void Rerun(string alias)
        {
            Vm.ResumeRootIndexCommand.Execute(Root(alias));
            Idle();
        }

        public List<string> Indexed(string folder) =>
        [
            .. Library.GetUnion().Where(r => Path.GetFileName(Path.TrimEndingDirectorySeparator(r.RootPath)) == folder)
                .Select(r => r.Item.FileName).Order(),
        ];

        public void Reopen()
        {
            Vm.PrepareForClose();
            Library.ShutdownAsync().GetAwaiter().GetResult();
            Library = new LibraryService(Data.Path);
            Vm = Open();
        }

        public void Dispose()
        {
            Vm.PrepareForClose();
            Library.ShutdownAsync().GetAwaiter().GetResult();
            Photos.Dispose();
            Data.Dispose();
        }
    }

    private static void Pump(int milliseconds) => Sta.PumpUntil(() => false, TimeSpan.FromMilliseconds(milliseconds));

    [Fact]
    public void AFolderWithOnlineOnlyFiles_AsksOnce_NamingItAndHowMany()
    {
        Sta.Run(() =>
        {
            using var f = new Fixture();
            f.Vm.AddFolder(f.Folder("A"), includeSubfolders: true);
            f.Idle();

            var question = Assert.Single(f.Asked);
            Assert.Equal(("A", 1), (question.Root.Alias, question.Count));
            Assert.Equal(["here.jpg"], f.Indexed("A")); // Skip
        });
    }

    [Fact]
    public void AnAnswer_HoldsForTheSession()
    {
        Sta.Run(() =>
        {
            using var f = new Fixture();
            f.Add("A");

            f.Rerun("A"); // it would read cloud.jpg again: the session's Skip holds

            Assert.Single(f.Asked);
            Assert.Equal(["here.jpg"], f.Indexed("A"));
            Assert.Equal(OnlineOnlyChoice.Skip, f.Root("A").OnlineOnly); // the menu shows the answer in force
        });
    }

    [Fact]
    public void Remember_KeepsItAfterARestart()
    {
        Sta.Run(() =>
        {
            using var f = new Fixture();
            f.Reply = _ => new OnlineOnlyAnswer(OnlineOnlyChoice.Skip, Remember: true);
            f.Add("A");

            f.Reopen();
            f.Rerun("A");

            Assert.Single(f.Asked);
            Assert.Equal(["here.jpg"], f.Indexed("A"));
            Assert.Equal(OnlineOnlyChoice.Skip, f.Root("A").OnlineOnly);
        });
    }

    [Fact]
    public void AnAnswerNotKept_IsAskedAgainAfterARestart()
    {
        Sta.Run(() =>
        {
            using var f = new Fixture();
            f.Add("A");

            f.Reopen();
            Assert.Null(f.Root("A").OnlineOnly);
            f.Rerun("A");

            Assert.Equal(2, f.Asked.Count);
        });
    }

    [Fact]
    public void ClosingTheDialog_IsSkip_ForTheSession_NotKept()
    {
        Sta.Run(() =>
        {
            using var f = new Fixture();
            f.Reply = _ => null; // Esc or ✕
            f.Add("A");
            f.Rerun("A");

            Assert.Single(f.Asked);
            Assert.Equal(["here.jpg"], f.Indexed("A"));

            f.Reopen();
            f.Rerun("A");
            Assert.Equal(2, f.Asked.Count);
        });
    }

    [Fact]
    public void Background_IndexesThem_AfterTheRest()
    {
        Sta.Run(() =>
        {
            using var f = new Fixture();
            f.Reply = _ => new OnlineOnlyAnswer(OnlineOnlyChoice.Background, Remember: false);

            f.Add("A");

            Assert.Equal(["cloud.jpg", "here.jpg"], f.Indexed("A"));
        });
    }

    [Fact]
    public void ChoosingFromTheMenu_KeepsIt()
    {
        Sta.Run(() =>
        {
            using var f = new Fixture();
            f.Add("A");

            f.Root("A").SetOnlineOnlyCommand.Execute("Now");
            f.Idle();

            Assert.Equal(OnlineOnlyChoice.Now, f.Root("A").OnlineOnly);
            f.Reopen();
            Assert.Equal(OnlineOnlyChoice.Now, f.Root("A").OnlineOnly);
            f.Rerun("A");
            Assert.Single(f.Asked);
        });
    }

    [Fact]
    public void FromSkipToBackground_ReindexesTheFolder_WithoutAsking()
    {
        Sta.Run(() =>
        {
            using var f = new Fixture();
            f.Add("A");
            Assert.Equal(["here.jpg"], f.Indexed("A"));

            f.Root("A").SetOnlineOnlyCommand.Execute("Background");
            f.Idle();

            Assert.Single(f.Asked);
            Assert.Equal(["cloud.jpg", "here.jpg"], f.Indexed("A"));
        });
    }

    [Fact]
    public void AskEachTime_ForgetsIt_AndStartsNothing()
    {
        Sta.Run(() =>
        {
            using var f = new Fixture();
            f.Reply = _ => new OnlineOnlyAnswer(OnlineOnlyChoice.Skip, Remember: true);
            f.Add("A");

            f.Root("A").SetOnlineOnlyCommand.Execute("Ask");

            Assert.False(f.Vm.IsIndexing);
            Assert.Null(f.Root("A").OnlineOnly);
            f.Reopen();
            Assert.Null(f.Root("A").OnlineOnly); // the kept answer is gone too
            f.Rerun("A");
            Assert.Equal(2, f.Asked.Count);
        });
    }

    [Fact]
    public void TwoFolders_AreAskedOneAfterTheOther()
    {
        Sta.Run(() =>
        {
            using var f = new Fixture();
            var open = 0;
            var most = 0;
            f.Reply = _ =>
            {
                most = Math.Max(most, ++open);
                Pump(400); // a modal dialog's own message loop: posted work runs meanwhile
                open--;
                return new OnlineOnlyAnswer(OnlineOnlyChoice.Skip, Remember: false);
            };

            f.Add("A", "B");

            Assert.Equal(2, f.Asked.Count);
            Assert.Equal(["A", "B"], f.Asked.Select(q => q.Root.Alias).Order());
            Assert.Equal(1, most);
        });
    }

    [Fact]
    public void TheSameFolder_Twice_IsAskedOnce()
    {
        Sta.Run(() =>
        {
            using var f = new Fixture();
            f.Reply = _ =>
            {
                if (f.Asked.Count == 1)
                {
                    f.Vm.ResumeRootIndexCommand.Execute(f.Root("A")); // another run of it, asking meanwhile
                    Pump(400);
                }
                return new OnlineOnlyAnswer(OnlineOnlyChoice.Background, Remember: false);
            };

            f.Add("A");

            Assert.Single(f.Asked);
            Assert.Equal(["cloud.jpg", "here.jpg"], f.Indexed("A"));
        });
    }

    [Fact]
    public void APausedFolder_GivesUpItsQuestion()
    {
        Sta.Run(() =>
        {
            using var f = new Fixture();
            f.Reply = q =>
            {
                var other = q.Root.Alias == "A" ? "B" : "A";
                if (f.Asked.Count == 1)
                {
                    f.Vm.PauseRootIndexCommand.Execute(f.Root(other)); // its question is waiting behind this one
                    Pump(400);
                }
                return new OnlineOnlyAnswer(OnlineOnlyChoice.Now, Remember: false);
            };

            f.Add("A", "B");

            var asked = Assert.Single(f.Asked).Root.Alias;
            var paused = asked == "A" ? "B" : "A";
            Assert.True(f.Root(paused).IsPaused);
            Assert.Empty(f.Indexed(paused)); // it ended at its question: nothing read
            Assert.Equal(["cloud.jpg", "here.jpg"], f.Indexed(asked));
        });
    }

    [Fact]
    public void ARemovedFolder_GivesUpItsQuestion_AndForgetsItsChoice()
    {
        Sta.Run(() =>
        {
            using var f = new Fixture();
            f.Reply = q =>
            {
                var other = q.Root.Alias == "A" ? "B" : "A";
                if (f.Asked.Count == 1)
                {
                    f.Vm.RemoveRootCommand.Execute(f.Root(other));
                    Pump(400);
                }
                return new OnlineOnlyAnswer(OnlineOnlyChoice.Skip, Remember: true);
            };

            f.Add("A", "B");

            var kept = Assert.Single(f.Asked).Root;
            Assert.Single(f.Vm.Roots);
            f.Vm.RemoveRootCommand.Execute(f.Root(kept.Alias));
            Assert.Null(new OnlineOnlyChoices(f.Library.Settings).Kept(kept.Id)); // C8
        });
    }

    [Fact]
    public void ALibraryClosing_GivesUpTheQuestionsWaiting()
    {
        Sta.Run(() =>
        {
            using var f = new Fixture();
            f.Reply = _ =>
            {
                f.Vm.PrepareForClose(); // a switch or exit while the first dialog is open
                Pump(400);
                return new OnlineOnlyAnswer(OnlineOnlyChoice.Now, Remember: true);
            };

            f.Vm.AddFolders([f.Folder("A"), f.Folder("B")]);
            Pump(1500);

            Assert.Single(f.Asked);
            Assert.True(f.Library.ShutdownAsync().Wait(Wait)); // nothing waits on a question
            Assert.Null(new OnlineOnlyChoices(new Core.Settings.SettingsService(f.Data.Path)).Kept(f.Asked[0].Root.Id)); // not recorded once closing
        });
    }
}
