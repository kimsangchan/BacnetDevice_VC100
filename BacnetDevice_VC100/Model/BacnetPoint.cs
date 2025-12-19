using System;
using System.IO.BACnet; // BACnet 라이브러리 참조 필요

namespace BacnetDevice_VC100.Model
{
    public class BacnetPoint
    {
        public string SystemPtId { get; set; }
        public BacnetObjectTypes ObjectType { get; set; }
        public uint ObjectInstance { get; set; }
        public uint DeviceInstance { get; set; } = 1;
        public float FailValue { get; set; } = 0.0f;

        // C# 7.3 호환 메서드
        public static Tuple<BacnetObjectTypes, uint> ParseSystemPtId(string systemPtId)
        {
            if (string.IsNullOrEmpty(systemPtId))
                throw new ArgumentException("Point ID empty");

            var parts = systemPtId.Split('-');
            if (parts.Length != 2)
                throw new ArgumentException("Invalid Point ID: " + systemPtId);

            string typeStr = parts[0].ToUpper();
            uint instance;
            if (!uint.TryParse(parts[1], out instance))
                throw new ArgumentException("Invalid Instance: " + parts[1]);

            BacnetObjectTypes type;
            switch (typeStr)
            {
                case "AI": type = BacnetObjectTypes.OBJECT_ANALOG_INPUT; break;
                case "AO": type = BacnetObjectTypes.OBJECT_ANALOG_OUTPUT; break;
                case "AV": type = BacnetObjectTypes.OBJECT_ANALOG_VALUE; break;
                case "BI": type = BacnetObjectTypes.OBJECT_BINARY_INPUT; break;
                case "BO": type = BacnetObjectTypes.OBJECT_BINARY_OUTPUT; break;
                case "BV": type = BacnetObjectTypes.OBJECT_BINARY_VALUE; break;
                case "MSI": type = BacnetObjectTypes.OBJECT_MULTI_STATE_INPUT; break;
                case "MSO": type = BacnetObjectTypes.OBJECT_MULTI_STATE_OUTPUT; break;
                case "MSV": type = BacnetObjectTypes.OBJECT_MULTI_STATE_VALUE; break;
                default: type = BacnetObjectTypes.OBJECT_ANALOG_VALUE; break;
            }

            return new Tuple<BacnetObjectTypes, uint>(type, instance);
        }
    }
}
