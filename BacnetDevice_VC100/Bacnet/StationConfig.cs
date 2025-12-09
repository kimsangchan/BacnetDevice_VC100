// Config/StationConfig.cs
namespace BacnetDevice_VC100.Bacnet
{
    public class StationConfig
    {
        public string Id { get; set; }
        public string Ip { get; set; }
        public int Port { get; set; }
        public uint DeviceId { get; set; }
    }
}
