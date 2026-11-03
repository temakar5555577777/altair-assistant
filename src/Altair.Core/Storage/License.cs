using Altair.Core.Models;

namespace Altair.Core.Storage;

/// <summary>
/// Точка расширения под продажу. Сейчас — «всё открыто» (личная версия).
/// Для коммерческой версии подставьте реализацию с сервером активации и офлайн-грейсом 72 ч.
/// </summary>
public interface ILicenseService
{
    bool IsActivated { get; }
    bool HasPlus { get; }
    DateTime? PlusUntil { get; }
    bool Allows(Tier tier) => tier == Tier.Free || HasPlus;
}

public sealed class OpenLicense : ILicenseService
{
    public bool IsActivated => true;
    public bool HasPlus => true;
    public DateTime? PlusUntil => null;
}
