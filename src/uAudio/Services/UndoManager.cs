using System.Collections.Generic;
using System.Linq;
using uAudioEditorPro.Models;

namespace uAudioEditorPro.Services
{
    public sealed class TrackSnapshot
    {
        public string TrackName { get; set; } = string.Empty;
        public float Volume { get; set; } = 1.0f;
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
        private static readonly Stack<ProjectSnapshot> _undoStack = new Stack<ProjectSnapshot>();
        private const int MaxUndoLevels = 50;

        public static void SaveSnapshot(ProjectSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return;
            }

            if (_undoStack.Count >= MaxUndoLevels)
            {
                List<ProjectSnapshot> retained = _undoStack.Reverse().Skip(1).Select(s => s.Clone()).ToList();
                _undoStack.Clear();

                foreach (ProjectSnapshot item in retained)
                {
                    _undoStack.Push(item);
                }
            }

            _undoStack.Push(snapshot.Clone());
        }

        public static ProjectSnapshot? PopSnapshot()
        {
            if (_undoStack.Count == 0)
            {
                return null;
            }

            return _undoStack.Pop().Clone();
        }

        public static bool CanUndo => _undoStack.Count > 0;

        public static int Count => _undoStack.Count;

        public static void Clear()
        {
            _undoStack.Clear();
        }
    }
}