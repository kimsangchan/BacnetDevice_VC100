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
using System.Collections.Concurrent;

namespace BacnetInventoryScanner
{
    public partial class MainForm : Form
    {
        private Button btnLoadDeviceList;
        private Button btnStartScan;
        private Button btnExport;

        private DataGridView dataGridView1;
        private List<SiDeviceInfo> _siDevices = new List<SiDeviceInfo>();
        private List<(string Ip, uint DeviceId)> _unregisteredDevices = new List<(string Ip, uint DeviceId)>();

        private BacnetInventoryService _inventoryService;
        // 날짜별 로그 생성을 위한 로거 (DeviceSeq 대신 0 또는 특정 ID 사용)
        private BacnetLogger _logger = new BacnetLogger(99999, LogLevel.INFO);
        private bool _isScanning;

        public MainForm()
        {
            InitializeComponent();
            // ⭐ [추가] 서비스 초기화
            _inventoryService = new BacnetInventoryService(_logger);
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
            if (_isScanning) return;
            _isScanning = true;
            btnStartScan.Text = "고속 스캔 중...";

            try
            {
                var registeredIps = new HashSet<string>(_siDevices.Select(d => d.DeviceIp));
                var newDevicesBag = new System.Collections.Concurrent.ConcurrentBag<(string Ip, uint DeviceId)>();

                foreach (var d in _siDevices) d.IsOnline = false;

                await Task.Run(() => {
                    var ipList = Enumerable.Range(1, 254).Select(i => $"172.16.130.{i}").ToList();

                    // [속도 최적화] 병렬 처리 수준을 50으로 상향
                    Parallel.ForEach(ipList, new ParallelOptions { MaxDegreeOfParallelism = 50 }, (ip) => {
                        // DirectScanDevice 내부 타임아웃이 짧아야 속도가 납니다.
                        var res = _inventoryService.DirectScanDevice(ip).Result;

                        if (res.DeviceId != 0xFFFFFFFF)
                        {
                            var info = _inventoryService.GetDeviceInfo(res.Ip, res.DeviceId).Result;
                            string vName = info["Vendor"];

                            if (registeredIps.Contains(ip))
                            {
                                var device = _siDevices.FirstOrDefault(d => d.DeviceIp == ip);
                                if (device != null) device.IsOnline = true;
                                _logger.Info($"[Online] {ip} (ID:{res.DeviceId}) | {info["Model"]} | {info["DeviceName"]}");
                            }
                            else if (vName.IndexOf("Tridium", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                newDevicesBag.Add(res);
                                _logger.Warning($"[New] {ip} 발견 (Tridium)");
                            }
                        }
                    });
                });

                this.Invoke(new Action(() => { dataGridView1.Refresh(); }));
                _unregisteredDevices = newDevicesBag.ToList();
                MessageBox.Show("고속 스캔 완료");
            }
            finally { _isScanning = false; btnStartScan.Text = "네트워크 스캔"; }
        }

        private async void btnExport_Click(object sender, EventArgs e)
        {
            string masterPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Exports", "SI_MASTER");
            var onlineDevices = _siDevices.FindAll(d => d.IsOnline);

            if (onlineDevices.Count == 0) return;

            btnExport.Enabled = false;
            btnExport.Text = "델타 추출 중...";

            try
            {
                await Task.Run(async () => {
                    var files = Directory.GetFiles(masterPath, "*.csv");

                    foreach (var dev in onlineDevices)
                    {
                        // 유연한 파일 매핑 (Seq 포함 파일 찾기)
                        string foundMaster = files.FirstOrDefault(f => Path.GetFileName(f).Contains(dev.FixCodeNo));

                        if (!string.IsNullOrEmpty(foundMaster))
                        {
                            var points = await _inventoryService.HarvestPoints(dev.DeviceIp);
                            if (points != null)
                            {
                                // [변경] 기존 파일에 붙이지 않고 '새로운 델타 전용 파일' 생성
                                _inventoryService.ExportDeltaOnly(foundMaster, points, dev.DeviceId, int.Parse(dev.FixCodeNo));
                            }
                        }
                    }
                });
                MessageBox.Show("신규 포인트(Delta) 추출이 완료되었습니다.\nSI_DELTA_ONLY 폴더를 확인하세요.");
            }
            finally
            {
                btnExport.Enabled = true;
                btnExport.Text = "SI 포인트 생성";
            }
        }
    }
}