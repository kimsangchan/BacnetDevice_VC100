using System;
using System.Collections.Generic;
using System.Linq;
using System.IO.BACnet;
using BacnetDevice_VC100.Util;
using BacnetDevice_VC100.Protocol;
using BacnetDevice_VC100.Model;
using BacnetDevice_VC100.Data;
using System.Text;

namespace BacnetDevice_VC100.Test
{
    class Program
    {
        static void Main(string[] args)
        {
            string encrypted = "OXGdXW6Vuj6Hny7mwhmvgdieuhEhlJW6";
            string decrypted = PasswordDecryptor.Decrypt(encrypted);

            Console.WriteLine($"암호화: {encrypted}");
            Console.WriteLine($"복호화: {decrypted}");
            // 출력: "복호화: admin123!@#"

            string expected = "admin123!@#";

            Console.WriteLine("===== XOR 키 찾기 =====\n");

            // Base64 디코딩
            byte[] data = Convert.FromBase64String(encrypted);

            Console.WriteLine($"암호화된 값: {encrypted}");
            Console.WriteLine($"예상 값: {expected}");
            Console.WriteLine($"바이트 길이: {data.Length}\n");

            // 예상 값의 첫 글자와 XOR 해서 키 추정
            Console.WriteLine("===== 키 추정 (첫 글자 기준) =====");
            byte firstEncrypted = data[0];  // 0x39 = '9'
            byte firstExpected = (byte)'a';  // 0x61

            byte possibleKey = (byte)(firstEncrypted ^ firstExpected);
            Console.WriteLine($"첫 글자 XOR: 0x{firstEncrypted:X2} ^ 0x{firstExpected:X2} = 0x{possibleKey:X2} ('{(char)possibleKey}')");

            // 전체 글자로 키 추정
            Console.WriteLine("\n===== 전체 문자 기준 키 추정 =====");
            byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);

            for (int i = 0; i < Math.Min(data.Length, expectedBytes.Length); i++)
            {
                byte keyByte = (byte)(data[i] ^ expectedBytes[i]);
                Console.WriteLine($"[{i}] 0x{data[i]:X2} ^ 0x{expectedBytes[i]:X2} = 0x{keyByte:X2} ('{(char)keyByte}')");
            }

            Console.WriteLine("\n아무 키나 누르세요...");
            Console.ReadKey();
        }
    }
}
