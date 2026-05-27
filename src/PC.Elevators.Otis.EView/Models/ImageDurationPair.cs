namespace PC.Elevators.Otis.EView.Models;

/// <summary>Associates a local image file path with a display duration for use in upload workflows.</summary>
public class ImageDurationPair
{
    #region Public properties

    public string FilePath { get; set; }
    public int DurationSeconds { get; set; }

    #endregion

    #region Public constructors

    public ImageDurationPair(string filePath, int durationSeconds)
    {
        FilePath = filePath;
        DurationSeconds = durationSeconds;
    }

    #endregion
}
