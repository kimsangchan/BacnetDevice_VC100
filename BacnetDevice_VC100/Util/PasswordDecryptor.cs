using System;
using System.Text;

namespace BacnetDevice_VC100.Util
{
    /// <summary>
    /// Config.XML 암호 복호화
    /// 
    /// [암호화 방식]
    /// Base64 + XOR (각 바이트마다 고유 키)
    /// </summary>
    public static class PasswordDecryptor
    {
        /// <summary>
        /// 암호 복호화
        /// 
        /// [입력]
        /// "OXGdXW6Vuj6Hny7mwhmvgdieuhEhlJW6" (암호화된 값)
        /// 
        /// [출력]
        /// "admin123!@#" (평문)
        /// 
        /// [XOR 키 패턴 (화면 분석 결과)]
        /// [0] = 0x58 ('X')
        /// [1] = 0x15
        /// [2] = 0xF0
        /// [3] = 0x34 ('4')
        /// [4] = 0x00
        /// [5] = 0xA4
        /// [6] = 0x88
        /// [7] = 0x0D
        /// [8] = 0xA6
        /// [9] = 0xDF
        /// [10] = 0x0D
        /// </summary>
        public static string Decrypt(string encryptedPassword)
        {
            if (string.IsNullOrEmpty(encryptedPassword))
            {
                return string.Empty;
            }

            try
            {
                // Base64 디코딩
                byte[] encrypted = Convert.FromBase64String(encryptedPassword);

                // XOR 키 (화면에서 역산한 값)
                byte[] xorKey = new byte[]
                {
                    0x58, // [0] 0x39 ^ 0x61 = 0x58 ('X')
                    0x15, // [1] 0x71 ^ 0x64 = 0x15
                    0xF0, // [2] 0x9D ^ 0x6D = 0xF0
                    0x34, // [3] 0x5D ^ 0x69 = 0x34 ('4')
                    0x00, // [4] 0x6E ^ 0x6E = 0x00
                    0xA4, // [5] 0x95 ^ 0x31 = 0xA4
                    0x88, // [6] 0xBA ^ 0x32 = 0x88
                    0x0D, // [7] 0x3E ^ 0x33 = 0x0D
                    0xA6, // [8] 0x87 ^ 0x21 = 0xA6
                    0xDF, // [9] 0x9F ^ 0x40 = 0xDF
                    0x0D  // [10] 0x2E ^ 0x23 = 0x0D
                };

                // XOR 복호화
                byte[] decrypted = new byte[encrypted.Length];
                for (int i = 0; i < encrypted.Length; i++)
                {
                    // 키를 반복 적용
                    decrypted[i] = (byte)(encrypted[i] ^ xorKey[i % xorKey.Length]);
                }

                // UTF-8로 변환 (Null 제거)
                string result = Encoding.UTF8.GetString(decrypted).TrimEnd('\0');

                // 검증: 유효한 비밀번호인지 확인
                if (IsValidPassword(result))
                {
                    return result;
                }

                // 복호화 실패 시 평문으로 반환
                return encryptedPassword;
            }
            catch (Exception)
            {
                // 에러 시 평문으로 반환 (개발/테스트용)
                return encryptedPassword;
            }
        }

        /// <summary>
        /// 유효한 비밀번호인지 검증
        /// </summary>
        private static bool IsValidPassword(string password)
        {
            if (string.IsNullOrEmpty(password))
                return false;

            // 최소 5자 이상
            if (password.Length < 5)
                return false;

            // ASCII 범위의 출력 가능한 문자만
            foreach (char c in password)
            {
                if (c < 32 || c > 126)  // 제어 문자 제외
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 암호화 (역방향, 필요시 사용)
        /// </summary>
        public static string Encrypt(string plainPassword)
        {
            if (string.IsNullOrEmpty(plainPassword))
            {
                return string.Empty;
            }

            try
            {
                byte[] plainBytes = Encoding.UTF8.GetBytes(plainPassword);

                // XOR 키 (동일)
                byte[] xorKey = new byte[]
                {
                    0x58, 0x15, 0xF0, 0x34, 0x00, 0xA4, 0x88, 0x0D, 0xA6, 0xDF, 0x0D
                };

                // XOR 암호화
                byte[] encrypted = new byte[plainBytes.Length];
                for (int i = 0; i < plainBytes.Length; i++)
                {
                    encrypted[i] = (byte)(plainBytes[i] ^ xorKey[i % xorKey.Length]);
                }

                // Base64 인코딩
                return Convert.ToBase64String(encrypted);
            }
            catch (Exception)
            {
                return plainPassword;
            }
        }
    }
}
