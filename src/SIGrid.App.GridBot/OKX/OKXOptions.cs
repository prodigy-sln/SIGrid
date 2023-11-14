using System.ComponentModel.DataAnnotations;

namespace SIGrid.App.GridBot.OKX;

public class OKXOptions
{
    [Required]
    public string ApiKey { get; set; } = null!;
    
    [Required]
    public string ApiSecret { get; set; } = null!;
    
    [Required]
    public string ApiPassPhrase { get; set; } = null!;
}
