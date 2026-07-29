using uAudio;
using uAudio.Models;
using uAudio.Services;
using Xunit;

namespace uAudio.Tests;

public sealed class UndoManagerTests : IDisposable
{
    public UndoManagerTests()
    {
        UndoManager.Clear();
    }

    public void Dispose()
    {
        UndoManager.Clear();
    }

    [Fact]
    public void SaveSnapshot_ClonesInput()
    {
        var clip = new AudioClipModel("a.wav", 0, 0, 1);
        var snapshot = new ProjectSnapshot
        {
            Tracks =
            {
                new TrackSnapshot
                {
                    TrackName = "Track 1",
                    Clips = { clip }
                }
            }
        };

        UndoManager.SaveSnapshot(snapshot);
        clip.StartTime = 10;
        snapshot.Tracks[0].TrackName = "Changed";

        ProjectSnapshot? restored = UndoManager.PopSnapshot();

        Assert.NotNull(restored);
        Assert.Equal("Track 1", restored!.Tracks[0].TrackName);
        Assert.Equal(0, restored.Tracks[0].Clips[0].StartTime);
    }

    [Fact]
    public void SaveSnapshot_TrimsToConfiguredLimit()
    {
        for (int i = 0; i < AppConstants.MaxUndoLevels + 10; i++)
        {
            UndoManager.SaveSnapshot(new ProjectSnapshot
            {
                Tracks =
                {
                    new TrackSnapshot { TrackName = $"Track {i}" }
                }
            });
        }

        Assert.Equal(AppConstants.MaxUndoLevels, UndoManager.Count);
    }

    [Fact]
    public async Task SaveAndPop_AreThreadSafe()
    {
        var tasks = Enumerable.Range(0, 8)
            .Select(worker => Task.Run(() =>
            {
                for (int i = 0; i < 200; i++)
                {
                    UndoManager.SaveSnapshot(new ProjectSnapshot
                    {
                        Tracks =
                        {
                            new TrackSnapshot { TrackName = $"{worker}-{i}" }
                        }
                    });

                    if (i % 3 == 0)
                    {
                        _ = UndoManager.PopSnapshot();
                    }
                }
            }))
            .ToArray();

        await Task.WhenAll(tasks);

        Assert.InRange(UndoManager.Count, 0, AppConstants.MaxUndoLevels);
    }
}
