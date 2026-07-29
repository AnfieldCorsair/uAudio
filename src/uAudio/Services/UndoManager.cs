using System.Collections.Generic;
using System.Linq;
using uAudio;
using uAudio.Models;

namespace uAudio.Services
{
    public sealed class TrackSnapshot
    {
        public string TrackName { get; set; } = string.Empty;
        public float Volume { get; set; } = AppConstants.DefaultVolume;
        public bool IsMuted { get; set; }
        public List<AudioClipModel> Clips { get; set; } = new List<AudioClipModel>();

        public TrackSnapshot Clone()
        {
            return new TrackSnapshot
            {
                TrackName = TrackName,
                Volume = Volume,
                IsMuted = IsMuted,
                Clips = Clips.Select(c => c.Clone()).ToList()
            };
        }
    }

    public sealed class ProjectSnapshot
    {
        public List<TrackSnapshot> Tracks { get; set; } = new List<TrackSnapshot>();

        public ProjectSnapshot Clone()
        {
            return new ProjectSnapshot
            {
                Tracks = Tracks.Select(t => t.Clone()).ToList()
            };
        }
    }

    public static class UndoManager
    {
        private static readonly object SyncRoot = new object();
        private static readonly Stack<ProjectSnapshot> UndoStack = new Stack<ProjectSnapshot>();

        public static void SaveSnapshot(ProjectSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return;
            }

            ProjectSnapshot clonedSnapshot = snapshot.Clone();

            lock (SyncRoot)
            {
                if (UndoStack.Count >= AppConstants.MaxUndoLevels)
                {
                    List<ProjectSnapshot> retained = UndoStack
                        .Reverse()
                        .Skip(1)
                        .Select(s => s.Clone())
                        .ToList();

                    UndoStack.Clear();

                    foreach (ProjectSnapshot item in retained)
                    {
                        UndoStack.Push(item);
                    }
                }

                UndoStack.Push(clonedSnapshot);
            }
        }

        public static ProjectSnapshot? PopSnapshot()
        {
            lock (SyncRoot)
            {
                if (UndoStack.Count == 0)
                {
                    return null;
                }

                return UndoStack.Pop().Clone();
            }
        }

        public static bool CanUndo
        {
            get
            {
                lock (SyncRoot)
                {
                    return UndoStack.Count > 0;
                }
            }
        }

        public static int Count
        {
            get
            {
                lock (SyncRoot)
                {
                    return UndoStack.Count;
                }
            }
        }

        public static void Clear()
        {
            lock (SyncRoot)
            {
                UndoStack.Clear();
            }
        }
    }
}
