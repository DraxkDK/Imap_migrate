namespace MDaemonCalendarMigration.Models;

public class ParsedEvent
{
    public string? Uid         { get; set; }
    public string? Summary     { get; set; }
    public string? Description { get; set; }
    public string? Location    { get; set; }
    public string? DtStart     { get; set; }
    public string? DtEnd       { get; set; }
    public string? DtStartTzId { get; set; }
    public string? DtEndTzId   { get; set; }
    public bool    IsAllDay    { get; set; }
    public string? Transp      { get; set; }  // TRANSPARENT / OPAQUE
    public string? Class       { get; set; }  // PUBLIC / PRIVATE / CONFIDENTIAL
    public string? Status      { get; set; }  // CONFIRMED / TENTATIVE / CANCELLED
    public string? RRule       { get; set; }
    public string  RawBlock    { get; set; } = "";
}
