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
        // ==========================================
        // [1] 필드 선언부 (클래스 상단 변수 있는 곳)
        // ==========================================
        private Button btnLoadDeviceList;
        private Button btnStartScan;
        private Button btnExport;
        private Button btnCheckHistory;

        // [추가] 각 설비별 Parent ID 상수 (전력용 ID는 확인 필요, 일단 임시값 넣음)
        private const string PARENT_ID_FACILITY = "4311744512"; // 기계/공조
        private const string PARENT_ID_POWER = "4311810304"; // 전력 (⚡확인필요!)

        // ▼ [추가] 대역 선택용 콤보박스
        private ComboBox cboSubnet;

        private DataGridView dataGridView1;
        private List<SiDeviceInfo> _siDevices = new List<SiDeviceInfo>();
        private List<(string Ip, uint DeviceId)> _unregisteredDevices = new List<(string Ip, uint DeviceId)>();

        private BacnetInventoryService _inventoryService;
        // 날짜별 로그 생성을 위한 로거 (DeviceSeq 대신 0 또는 특정 ID 사용)
        private BacnetLogger _logger = new BacnetLogger(99999, LogLevel.INFO);
        private bool _isScanning;

        public MainForm()
        {
            // ==========================================
            // [2] 생성자 내부 (UI 속성 및 이벤트 연결)
            // InitializeComponent(); 바로 아래에 넣으세요.
            // ==========================================
            InitializeComponent();
            // ⭐ [추가] 서비스 초기화
            _inventoryService = new BacnetInventoryService(_logger);
            this.Size = new Size(1000, 700);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Text = "BACnet Inventory Manager (Toss Style)";

            InitDynamicControls();
            _logger.Info("--- 어플리케이션 시작 ---");
        }

        // [MainForm.cs]
        private void InitDynamicControls()
        {
            this.BackColor = Color.FromArgb(242, 245, 248);
            Panel pnlHeader = new Panel { Dock = DockStyle.Top, Height = 80, Padding = new Padding(20) };

            // 1. [추가] 대역 선택 콤보박스 생성
            cboSubnet = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("맑은 고딕", 12, FontStyle.Bold),
                Size = new Size(250, 45), // 너비 250
                Location = new Point(20, 17)
            };

            // 2. [추가] 스캔할 대역 목록 등록 (필요하면 더 추가하세요)
            cboSubnet.Items.Add("172.16.130.x (자동제어_1캠퍼스)");
            cboSubnet.Items.Add("172.16.132.x (자동제어_2캠퍼스)");
            cboSubnet.Items.Add("192.168.134.x (전력망)");

            // 기본값 선택 (0:기계, 1:공조, 2:전력)
            cboSubnet.SelectedIndex = 2;

            // 3. [수정] 버튼 위치 조정 (콤보박스 뒤로 밀기: X 좌표 +270씩 이동)
            // 기존 0 -> 290
            btnLoadDeviceList = CreateTossButton("장비 목록 로드", Color.FromArgb(0, 100, 255), 290);
            // 기존 170 -> 460
            btnStartScan = CreateTossButton("네트워크 스캔", Color.FromArgb(48, 199, 150), 460);
            // 기존 340 -> 630
            btnExport = CreateTossButton("SI 포인트 생성", Color.FromArgb(107, 118, 132), 630);
            // 기존 510 -> 800
            btnCheckHistory = CreateTossButton("변경 이력", Color.FromArgb(255, 140, 0), 800);

            // 이벤트 연결 (기존 유지)
            btnLoadDeviceList.Click += btnLoadDeviceList_Click;
            btnStartScan.Click += btnStartScan_Click;
            btnExport.Click += btnExport_Click;
            btnCheckHistory.Click += btnCheckHistory_Click;

            // 패널에 컨트롤 추가
            pnlHeader.Controls.Add(cboSubnet); // 콤보박스 추가
            pnlHeader.Controls.Add(btnExport);
            pnlHeader.Controls.Add(btnStartScan);
            pnlHeader.Controls.Add(btnLoadDeviceList);
            pnlHeader.Controls.Add(btnCheckHistory);

            // 그리드 설정 (기존 유지)
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

        // [이벤트 핸들러] 변경사항 추적 로직 실행
        // [이벤트] '변경 이력' 버튼 클릭 시 실행됨
        private async void btnCheckHistory_Click(object sender, EventArgs e)
        {
            // 1. 온라인(통신 가능) 상태인 장비만 추려냄
            var onlineDevices = _siDevices.FindAll(d => d.IsOnline);
            if (onlineDevices.Count == 0)
            {
                MessageBox.Show("온라인 장비가 없습니다. 먼저 [네트워크 스캔]을 진행하세요.");
                return;
            }

            // 2. UI 잠금 및 상태 표시
            btnCheckHistory.Enabled = false;
            btnCheckHistory.Text = "분석 중...";
            _logger.Info("--- [Start] 변경 사항(History) 및 SQL 분석 시작 ---");

            try
            {
                await Task.Run(async () => {
                    // SI_MASTER 폴더 경로 확보
                    string masterDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Exports", "SI_MASTER");
                    // 폴더 내 모든 CSV 파일을 미리 읽어둠 (성능 최적화)
                    var files = Directory.GetFiles(masterDir, "*.csv");

                    foreach (var dev in onlineDevices)
                    {
                        // [로직] 파일명에 장비코드(FixCodeNo)가 포함된 마스터 파일을 찾음 (유연한 매핑)
                        string masterFile = files.FirstOrDefault(f => Path.GetFileName(f).Contains(dev.FixCodeNo));

                        if (!string.IsNullOrEmpty(masterFile))
                        {
                            // [통신] 장비에서 현재 포인트 리스트 수집
                            var currentPoints = await _inventoryService.HarvestPoints(dev.DeviceIp);

                            if (currentPoints != null)
                            {
                                // [핵심] 서비스 계층의 변경 감지 로직 호출 (Update/Delete/SQL 생성)
                                _inventoryService.DetectChangesAndGenerateSql(masterFile, currentPoints, dev.DeviceId, int.Parse(dev.FixCodeNo));
                            }
                        }
                    }
                });

                // 3. 완료 처리 및 폴더 열기
                MessageBox.Show("분석 완료!\nExports\\HISTORY 폴더에 결과가 저장되었습니다.");
                System.Diagnostics.Process.Start("explorer.exe", Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Exports", "HISTORY"));
            }
            catch (Exception ex)
            {
                _logger.Error("히스토리 분석 중 치명적 오류 발생", ex);
            }
            finally
            {
                // UI 복구
                btnCheckHistory.Enabled = true;
                btnCheckHistory.Text = "변경 이력";
            }
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
        // [MainForm.cs]
        private void btnLoadDeviceList_Click(object sender, EventArgs e)
        {
            try
            {
                // 1. 현재 선택된 대역 확인
                // 0:기계(172..130), 1:공조(172..132), 2:전력(192..134)
                int selectedIndex = cboSubnet.SelectedIndex;

                string targetParentId = "";
                string targetIpPrefix = "";

                switch (selectedIndex)
                {
                    case 0: // 기계
                        targetParentId = PARENT_ID_FACILITY;
                        targetIpPrefix = "172.16.130";
                        break;
                    case 1: // 공조
                        targetParentId = PARENT_ID_FACILITY; // 공조도 설비에 속하면 동일 ID 사용
                        targetIpPrefix = "172.16.132";
                        break;
                    case 2: // 전력
                        targetParentId = PARENT_ID_POWER;    // ⭐ 전력용 ID 적용
                        targetIpPrefix = "192.168.134";
                        break;
                    default:
                        targetParentId = PARENT_ID_FACILITY;
                        break;
                }

                string connStr = "Server=192.168.131.127;Database=IBSInfo;User Id=sa;Password=admin123!@#;";
                using (SqlConnection conn = new SqlConnection(connStr))
                {
                    conn.Open();

                    // ⭐ [수정] 동적 Parent ID 적용
                    string sql = $"SELECT FIX_CODENO, DEVICE_ID, CODE_NAME, DEVICE_IP FROM P_OBJ_CODE WHERE MULTI_PARENT_ID = '{targetParentId}'";

                    // 만약 IP 대역으로도 DB를 필터링하고 싶다면 아래 주석 해제
                    // sql += $" AND DEVICE_IP LIKE '{targetIpPrefix}%'";

                    SqlCommand cmd = new SqlCommand(sql, conn);
                    SqlDataReader reader = cmd.ExecuteReader();

                    _siDevices.Clear(); // 기존 리스트 초기화

                    while (reader.Read())
                    {
                        _siDevices.Add(new SiDeviceInfo
                        {
                            FixCodeNo = reader["FIX_CODENO"].ToString(),
                            DeviceId = Convert.ToInt32(reader["DEVICE_ID"]),
                            CodeName = reader["CODE_NAME"].ToString(),
                            DeviceIp = reader["DEVICE_IP"].ToString(),
                            IsOnline = false // 로드 시점엔 오프라인으로 가정
                        });
                    }

                    // 그리드 갱신
                    RefreshGrid();
                    MessageBox.Show($"[{cboSubnet.Text}] DB 목록 로드 완료.\n총 {_siDevices.Count}대");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("DB 에러: " + ex.Message);
            }
        }

        // 그리드 갱신용 헬퍼 함수
        private void RefreshGrid()
        {
            // 화면 깜빡임 방지
            dataGridView1.DataSource = null;
            // 리스트를 복사해서 바인딩 (UI 스레드 안전성)
            dataGridView1.DataSource = new List<SiDeviceInfo>(_siDevices);

            // 컬럼 정리 (보기 좋게)
            if (dataGridView1.Columns["MultiParentId"] != null) dataGridView1.Columns["MultiParentId"].Visible = false;
        }

        // MainForm.cs 내부의 스캔 버튼 이벤트
        // MainForm.cs 내부 수정
        // [MainForm.cs]
        // [MainForm.cs]
        // ▼ [수정] 2. 스캔 버튼 로직 (이름 구분 처리)
        private async void btnStartScan_Click(object sender, EventArgs e)
        {
            if (_isScanning) return;

            string selectedText = cboSubnet.SelectedItem.ToString();
            string subnetPrefix = selectedText.Split('x')[0].Trim().TrimEnd('.');

            _isScanning = true;
            btnStartScan.Text = $"스캔 중 ({subnetPrefix})...";

            try
            {
                // 초기화: 온라인 상태 리셋, 실제 이름 리셋
                foreach (var d in _siDevices)
                {
                    d.IsOnline = false;
                    d.RealDeviceName = ""; // 스캔 전엔 모름
                }

                await Task.Run(() => {
                    var ipList = Enumerable.Range(1, 254).Select(i => $"{subnetPrefix}.{i}").ToList();

                    Parallel.ForEach(ipList, new ParallelOptions { MaxDegreeOfParallelism = 50 }, (ip) => {

                        var res = _inventoryService.DirectScanDevice(ip).Result;

                        if (res.DeviceId != 0xFFFFFFFF) // 장비 발견
                        {
                            var info = _inventoryService.GetDeviceInfo(res.Ip, res.DeviceId).Result;

                            // ⭐ 실제 장비 이름 (없으면 Unknown)
                            string realName = info["DeviceName"];
                            string vendor = info["Vendor"];

                            lock (_siDevices)
                            {
                                var existDevice = _siDevices.FirstOrDefault(d => d.DeviceIp == ip);

                                if (existDevice != null)
                                {
                                    // [A] DB 매칭 성공 (기존 장비)
                                    existDevice.IsOnline = true;
                                    existDevice.RealDeviceName = realName; // ⭐ 실제 이름 업데이트

                                    // 로그: [매칭] IP | ID | DB명 vs 실제명
                                    _logger.Info($"[매칭] {ip} (ID:{res.DeviceId}) | DB:{existDevice.CodeName} | Real:{realName}");
                                }
                                else
                                {
                                    // [B] 신규 장비 (DB 없음)
                                    _siDevices.Add(new SiDeviceInfo
                                    {
                                        FixCodeNo = "NEW",
                                        DeviceId = (int)res.DeviceId,
                                        DeviceIp = ip,

                                        // ⭐ 명확한 구분
                                        CodeName = "(DB미등록)",   // DB 이름은 없음
                                        RealDeviceName = realName, // 실제 이름만 존재

                                        IsOnline = true,
                                        MultiParentId = "UNKNOWN"
                                    });

                                    _logger.Warning($"[신규] {ip} (ID:{res.DeviceId}) | Real:{realName} | Vendor:{vendor}");
                                }
                            }
                        }
                    });
                });

                this.Invoke(new Action(() => {
                    RefreshGrid();

                    // 보기 좋게 정렬 (IP 순서)
                    var sorted = _siDevices.OrderBy(d =>
                    {
                        Version v;
                        return Version.TryParse(d.DeviceIp, out v) ? v : new Version("0.0.0.0");
                    }).ToList();

                    dataGridView1.DataSource = sorted;

                    // ⭐ [UI] 컬럼 순서 및 헤더 정리 (보기 좋게)
                    if (dataGridView1.Columns["CodeName"] != null)
                        dataGridView1.Columns["CodeName"].HeaderText = "DB 명칭";

                    if (dataGridView1.Columns["RealDeviceName"] != null)
                    {
                        dataGridView1.Columns["RealDeviceName"].HeaderText = "실제 장비명 (Scan)";
                        dataGridView1.Columns["RealDeviceName"].DisplayIndex = 3; // IP 뒤쪽으로 이동
                    }

                }));

                MessageBox.Show($"스캔 완료.\n목록: {_siDevices.Count}대 (Online: {_siDevices.Count(d => d.IsOnline)})");
            }
            catch (Exception ex)
            {
                _logger.Error("스캔 오류", ex);
                MessageBox.Show("오류: " + ex.Message);
            }
            finally
            {
                _isScanning = false;
                btnStartScan.Text = "네트워크 스캔";
            }
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