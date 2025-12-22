using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using System.Data.SqlClient;
using BacnetInventoryScanner.Models;
using System.Threading.Tasks;
using BacnetDevice_VC100.Util;
using System.Text;
using System.IO;
using System.Linq;
using BacnetInventoryScanner.Service;

namespace BacnetInventoryScanner
{
    public partial class MainForm : Form
    {
        private Button btnLoadDeviceList;
        private Button btnStartScan;
        private Button btnExport;
        private DataGridView dataGridView1;
        private List<SiDeviceInfo> _siDevices = new List<SiDeviceInfo>();

        // 날짜별 로그 생성을 위한 로거 (DeviceSeq 대신 0 또는 특정 ID 사용)
        private BacnetLogger _logger = new BacnetLogger(99999, LogLevel.INFO);

        public MainForm()
        {
            InitializeComponent();
            this.Size = new Size(1000, 700);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Text = "BACnet Inventory Manager (Toss Style)";

            InitDynamicControls();
            _logger.Info("--- 어플리케이션 시작 ---");
        }

        private void InitDynamicControls()
        {
            this.BackColor = Color.FromArgb(242, 245, 248);
            Panel pnlHeader = new Panel { Dock = DockStyle.Top, Height = 80, Padding = new Padding(20) };

            btnLoadDeviceList = CreateTossButton("장비 목록 로드", Color.FromArgb(0, 100, 255), 0);
            btnStartScan = CreateTossButton("네트워크 스캔", Color.FromArgb(48, 199, 150), 170);
            btnExport = CreateTossButton("SI 포인트 생성", Color.FromArgb(107, 118, 132), 340);

            // ⭐ [필수] 클릭 이벤트 연결 (기존에 빠져있던 부분)
            btnLoadDeviceList.Click += btnLoadDeviceList_Click;
            btnStartScan.Click += btnStartScan_Click; // 이 줄이 있어야 버튼이 동작합니다.
            btnExport.Click += btnExport_Click; // 이 줄이 빠져있을 확률이 높습니다.

            pnlHeader.Controls.Add(btnExport);
            pnlHeader.Controls.Add(btnStartScan);
            pnlHeader.Controls.Add(btnLoadDeviceList);

            dataGridView1 = new DataGridView
            {
                Dock = DockStyle.Fill,
                BackgroundColor = Color.White,
                BorderStyle = BorderStyle.None,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                RowHeadersVisible = false,
                AllowUserToAddRows = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect
            };

            this.Controls.Add(dataGridView1);
            this.Controls.Add(pnlHeader);
        }

        private Button CreateTossButton(string text, Color color, int left)
        {
            return new Button
            {
                Text = text,
                BackColor = color,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("맑은 고딕", 10, FontStyle.Bold),
                Size = new Size(150, 45),
                Location = new Point(20 + left, 17),
                Cursor = Cursors.Hand
            };
        }

        // DB 로드 로직
        private void btnLoadDeviceList_Click(object sender, EventArgs e)
        {
            try
            {
                // 실제 배포 시에는 PasswordDecryptor를 사용하여 복호화된 PW를 넣으세요.
                string connStr = "Server=192.168.131.127;Database=IBSInfo;User Id=sa;Password=admin123!@#;";

                using (SqlConnection conn = new SqlConnection(connStr))
                {
                    conn.Open();
                    // 사용자가 주신 MULTI_PARENT_ID 조건 적용
                    string sql = "SELECT FIX_CODENO, DEVICE_ID, CODE_NAME, DEVICE_IP FROM P_OBJ_CODE WHERE MULTI_PARENT_ID = '4311744512'";

                    SqlCommand cmd = new SqlCommand(sql, conn);
                    SqlDataReader reader = cmd.ExecuteReader();

                    _siDevices.Clear();
                    while (reader.Read())
                    {
                        _siDevices.Add(new SiDeviceInfo
                        {
                            FixCodeNo = reader["FIX_CODENO"].ToString(),
                            DeviceId = Convert.ToInt32(reader["DEVICE_ID"]),
                            CodeName = reader["CODE_NAME"].ToString(),
                            DeviceIp = reader["DEVICE_IP"].ToString()
                        });
                    }

                    dataGridView1.DataSource = null;
                    dataGridView1.DataSource = _siDevices;
                    MessageBox.Show($"{_siDevices.Count}개의 장비를 불러왔습니다.");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("DB 에러: " + ex.Message);
            }
        }

        // MainForm.cs 내부의 스캔 버튼 이벤트
        // MainForm.cs 내부 수정
        private async void btnStartScan_Click(object sender, EventArgs e)
        {
            if (_siDevices == null || _siDevices.Count == 0)
            {
                _logger.Warning("스캔 시도 실패: 로드된 장비 없음");
                MessageBox.Show("먼저 [장비 목록 로드]를 실행해 주세요.");
                return;
            }

            btnStartScan.Enabled = false;
            btnStartScan.Text = "스캔 중...";
            _logger.Info($"--- 실전 BACnet 스캔 시작: 대상 {_siDevices.Count}대 ---");

            try
            {
                // 고속 스캔을 위해 10개씩 병렬 처리
                await Task.Run(async () => {
                    // Niagara Station과 충돌을 피하기 위해 임시 포트 사용
                    using (var udp = new System.Net.Sockets.UdpClient(0))
                    {
                        udp.Client.ReceiveTimeout = 1000; // 응답 대기 시간 1초

                        // Who-Is 표준 패킷 (Unicast용)
                        byte[] whoIsPayload = new byte[] { 0x81, 0x0a, 0x00, 0x0c, 0x01, 0x20, 0xff, 0xff, 0x00, 0xff, 0x10, 0x08 };

                        foreach (var dev in _siDevices)
                        {
                            try
                            {
                                _logger.Info($"[Scan] {dev.CodeName} ({dev.DeviceIp}) 시도 중...");

                                var remoteEP = new System.Net.IPEndPoint(System.Net.IPAddress.Parse(dev.DeviceIp), 47808);
                                await udp.SendAsync(whoIsPayload, whoIsPayload.Length, remoteEP);

                                // 응답 확인 (비동기 수신)
                                var receiveTask = udp.ReceiveAsync();
                                if (await Task.WhenAny(receiveTask, Task.Delay(800)) == receiveTask)
                                {
                                    var result = receiveTask.Result;
                                    if (result.Buffer.Length > 0)
                                    {
                                        dev.IsOnline = true; // 대답이 오면 온라인!
                                        _logger.Info($"[Success] {dev.DeviceIp} 응답 확인 완료");
                                    }
                                }
                                else
                                {
                                    dev.IsOnline = false;
                                    _logger.Warning($"[Fail] {dev.DeviceIp} 응답 없음 (Timeout)");
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.Error($"[Error] {dev.DeviceIp} 스캔 중 오류", ex);
                            }
                        }
                    }
                });

                // UI 갱신 (온라인인 장비는 그리드에서 확인 가능)
                dataGridView1.Refresh();
                _logger.Info("--- 실전 스캔 완료 ---");
                MessageBox.Show("스캔이 완료되었습니다. 응답이 있는 장비를 확인하세요.");
            }
            catch (Exception ex)
            {
                _logger.Error("전체 스캔 루프 치명적 에러", ex);
            }
            finally
            {
                btnStartScan.Enabled = true;
                btnStartScan.Text = "네트워크 스캔";
            }
        }
        // MainForm.cs 내부의 수출 버튼 이벤트
        // MainForm.cs 내부의 포인트 추출 버튼 로직 업데이트
        private async void btnExport_Click(object sender, EventArgs e)
        {
            var onlineDevices = _siDevices.FindAll(d => d.IsOnline);
            if (onlineDevices.Count == 0) return;

            btnExport.Enabled = false;
            btnExport.Text = "수집 중...";

            try
            {
                var service = new BacnetInventoryScanner.Service.BacnetInventoryService(_logger);

                await Task.Run(async () => {
                    string exportPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Exports");
                    if (!Directory.Exists(exportPath)) Directory.CreateDirectory(exportPath);

                    foreach (var dev in onlineDevices)
                    {
                        _logger.Info($"[UI] {dev.CodeName} 포인트 추출 시도...");

                        // [수정] 인자를 IP 하나만 전달하도록 변경 (CS1501 해결)
                        var points = await service.HarvestPoints(dev.DeviceIp);

                        string filePath = Path.Combine(exportPath, $"S-{dev.FixCodeNo}-{dev.CodeName}.csv");
                        service.ExportToSiCsv(filePath, points, int.Parse(dev.FixCodeNo), dev.DeviceId);

                        _logger.Info($"[UI] {dev.CodeName} 파일 생성 완료: {filePath}");
                    }
                });

                MessageBox.Show("수출 완료!");
                System.Diagnostics.Process.Start("explorer.exe", Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Exports"));
            }
            catch (Exception ex)
            {
                _logger.Error("에러 발생", ex);
            }
            finally
            {
                btnExport.Enabled = true;
                btnExport.Text = "SI 포인트 생성";
            }
        }
    }
}