namespace uAudio.Models
{
    // Класс, который хранит данные об одной дорожке
    public class AudioTrack
    {
        public string FilePath { get; set; }
        public float Volume { get; set; } = 1.0f; // По умолчанию 100%
        public bool IsMuted { get; set; } = false;

        public AudioTrack(string path)
        {
            FilePath = path;
        }
    }
}
