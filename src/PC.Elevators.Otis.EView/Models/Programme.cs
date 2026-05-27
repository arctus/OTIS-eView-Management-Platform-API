namespace PC.Elevators.Otis.EView.Models;

/// <summary>Represents a programme (content item) in the MPD system.</summary>
public class Programme
{
    public string ContentId { get; set; } = "";
    public string Name { get; set; } = "";
    public string DateOfInsertion { get; set; } = "";
    public int NumberOfImages { get; set; }
}
