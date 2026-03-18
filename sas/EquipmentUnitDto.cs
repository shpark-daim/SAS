using System.ComponentModel.DataAnnotations;

namespace sas;

public class EquipmentUnitCommandDto {
    [Required(ErrorMessage = "Required parameter")]
    public string? EqpUnitId { get; set; } = default;

    [Required(ErrorMessage = "Required parameter")]
    public string? MachineId { get; set; } = null;

    public string? Command { get; set; } = default;

    public string? TransportJobId { get; set; } = default;

    public string? RecipeId { get; set; } = default;
}