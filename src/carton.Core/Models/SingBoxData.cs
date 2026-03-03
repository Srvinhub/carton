namespace carton.Core.Models;

internal sealed class SingBoxData
{
    public int SelectedProfileId { get; set; }
    public List<Profile> Profiles { get; set; } = new();
}
