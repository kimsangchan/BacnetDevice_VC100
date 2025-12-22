using System;

public sealed class RawDeviceInfo
{
    public Guid SessionId { get; set; }
    public string Subnet { get; set; }
    public int DeviceId { get; set; }
    public string DeviceIp { get; set; }
    public int VendorId { get; set; }
    public int MaxApdu { get; set; }
    public byte Segmentation { get; set; }
}
