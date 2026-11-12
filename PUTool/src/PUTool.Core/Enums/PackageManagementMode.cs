namespace PUTool.Core.Enums;

public enum PackageManagementMode : byte
{
    Local = 0,
    Central,
    CentralOverride,
    Mixed,
    Transitive,
    Unknown
}