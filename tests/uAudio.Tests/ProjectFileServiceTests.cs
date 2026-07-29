using uAudio.Models;
using uAudio.Services;
using Xunit;

namespace uAudio.Tests;

public sealed class ProjectFileServiceTests
{
    [Fact]
    public void SaveAndLoad_ResolvesRelativeClipPaths()
    {
        string root = Path.Combine(Path.GetTempPath(), "uaudio-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string audioPath = Path.Combine(root, "clip.wav");
        File.WriteAllBytes(audioPath, new byte[] { 0, 1, 2, 3 });
        string projectPath = Path.Combine(root, "project.uaudioproj");

        var project = new ProjectFileModel
        {
            Tracks =
            {
                new ProjectTrackFileModel
                {
                    TrackName = "Track 1",
                    Clips =
                    {
                        new ProjectClipFileModel
                        {
                            FilePath = audioPath,
                            StartTime = 0,
                            Duration = 1
                        }
                    }
                }
            }
        };

        try
        {
            ProjectFileService.Save(projectPath, project);
            ProjectLoadResult loaded = ProjectFileService.Load(projectPath);

            Assert.Empty(loaded.MissingFiles);
            Assert.Equal(Path.GetFullPath(audioPath), loaded.Project.Tracks[0].Clips[0].FilePath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CreateStateSignature_IgnoresCurrentPlaybackTime()
    {
        var projectA = new ProjectFileModel { CurrentTime = 1 };
        var projectB = new ProjectFileModel { CurrentTime = 99 };

        Assert.Equal(
            ProjectFileService.CreateStateSignature(projectA),
            ProjectFileService.CreateStateSignature(projectB));
    }
}
