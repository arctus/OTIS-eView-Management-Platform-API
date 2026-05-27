namespace PC.Elevators.Otis.EView.Models;

/// <summary>Represents a scheduled transmission on a specific elevator display device.</summary>
public class ElevatorTransmission
{
    public string ProgrammingId { get; set; } = "";
    public string Location { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string DeviceHuecoId { get; set; } = "";
    public string Address { get; set; } = "";
    public string Town { get; set; } = "";
    public string Province { get; set; } = "";
    public string CurrentProgram { get; set; } = "";
    public string ContentId { get; set; } = "";
    public string ContentName { get; set; } = "";
    public string DateOfInsertion { get; set; } = "";
    public int NumberOfImages { get; set; }
    public string StartDate { get; set; } = "";
    public string EndDate { get; set; } = "";
    public string StartTime { get; set; } = "";
    public string EndTime { get; set; } = "";
    public string Priority { get; set; } = "";
    public string Days { get; set; } = "";
    public string TransmissionStatus { get; set; } = "";
}
