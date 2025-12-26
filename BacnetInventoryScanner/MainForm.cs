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


        // [MainForm.cs]

        private async void btnCheckHistory_Click(object sender, EventArgs e)
        {
            // 1. 온라인 장비 필터링
            var onlineDevices = _siDevices.FindAll(d => d.IsOnline);
            if (onlineDevices.Count == 0)
            {
                MessageBox.Show("온라인 장비가 없습니다. 먼저 [네트워크 스캔]을 진행하세요.");
                return;
            }

            // 2. UI 잠금
            btnCheckHistory.Enabled = false;
            btnCheckHistory.Text = "분석 중...";
            _logger.Info("--- [Start] 변경 사항(History) 및 SQL 분석 시작 ---");

            try
            {
                // 3. 비동기 병렬 분석 (CSV/SQL 개별 생성)
                await Task.Run(async () => {
                    string masterDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Exports", "SI_MASTER");
                    if (!Directory.Exists(masterDir)) Directory.CreateDirectory(masterDir);
                    var files = Directory.GetFiles(masterDir, "*.csv");

                    Parallel.ForEach(onlineDevices, new ParallelOptions { MaxDegreeOfParallelism = 10 }, (dev) => {
                        string masterFile = files.FirstOrDefault(f => Path.GetFileName(f).Contains(dev.FixCodeNo));

                        if (!string.IsNullOrEmpty(masterFile))
                        {
                            var currentPoints = _inventoryService.HarvestPoints(dev.DeviceIp).Result;
                            if (currentPoints != null && currentPoints.Count > 0)
                            {
                                // 개별 파일 생성
                                _inventoryService.DetectChangesAndGenerateSql(
                                    masterFile, currentPoints, (int)dev.BacnetId, dev.FixCodeNo);
                            }
                        }
                    });
                });

                // 4. [통합] CSV 리포트 병합
                string summaryCsv = _inventoryService.MergeTodayHistoryFiles();

                // 5. [통합] SQL 스크립트 병합 (추가됨)
                string summarySql = _inventoryService.MergeTodaySqlFiles();

                // 6. 결과 메시지 작성
                StringBuilder msg = new StringBuilder();
                msg.AppendLine("분석이 완료되었습니다.");

                if (!string.IsNullOrEmpty(summaryCsv))
                    msg.AppendLine($"- CSV 리포트: {Path.GetFileName(summaryCsv)}");

                if (!string.IsNullOrEmpty(summarySql))
                    msg.AppendLine($"- SQL 스크립트: {Path.GetFileName(summarySql)}");

                MessageBox.Show(msg.ToString(), "완료");

                // 탐색기 열기
                string historyPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Exports", "HISTORY");
                if (Directory.Exists(historyPath))
                {
                    System.Diagnostics.Process.Start("explorer.exe", historyPath);
                }
            }
            catch (Exception ex)
            {
                _logger.Error("히스토리 분석 중 오류", ex);
                MessageBox.Show("오류 발생: " + ex.Message);
            }
            finally
            {
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
        // [MainForm.cs]

        private void btnLoadDeviceList_Click(object sender, EventArgs e)
        {
            try
            {
                // 1. [복구] 콤보박스 선택에 따라 Parent ID 결정 (설비 vs 전력)
                int selectedIndex = cboSubnet.SelectedIndex;
                string targetParentId = "";
                string targetIpPrefix = ""; // 필요시 사용 (현재는 ParentID가 핵심)

                switch (selectedIndex)
                {
                    case 0: // 기계설비 (172.16.130.x)
                        targetParentId = PARENT_ID_FACILITY; // "4311744512"
                        targetIpPrefix = "172.16.130";
                        break;
                    case 1: // 공조설비 (172.16.132.x) - 보통 기계와 같은 ParentID 사용
                        targetParentId = PARENT_ID_FACILITY;
                        targetIpPrefix = "172.16.132";
                        break;
                    case 2: // 전력 (192.168.134.x)
                        targetParentId = PARENT_ID_POWER;    // "4311810304" (사용자님 확인 값)
                        targetIpPrefix = "192.168.134";
                        break;
                    default:
                        targetParentId = PARENT_ID_FACILITY;
                        break;
                }

                // 2. DB 연결 및 조회
                string connStr = "Server=192.168.131.127;Database=IBSInfo;User Id=sa;Password=admin123!@#;";
                using (SqlConnection conn = new SqlConnection(connStr))
                {
                    conn.Open();

                    // ⭐ [수정] SI 설정값(SERVER_ID, SYSTEM_CODE, DEVICE_ID)까지 모두 조회
                    // DEVICE_ID는 여기서 '정렬 순서'를 의미하는 값입니다.
                    string sql = $@"
                SELECT FIX_CODENO, SERVER_ID, SYSTEM_CODE, DEVICE_ID, CODE_NAME, DEVICE_IP 
                FROM P_OBJ_CODE 
                WHERE MULTI_PARENT_ID = '{targetParentId}'";

                    // 필요하다면 IP 대역으로 한 번 더 필터링 (선택 사항)
                    // sql += $" AND DEVICE_IP LIKE '{targetIpPrefix}%'";

                    SqlCommand cmd = new SqlCommand(sql, conn);
                    SqlDataReader reader = cmd.ExecuteReader();

                    _siDevices.Clear(); // 리스트 초기화

                    while (reader.Read())
                    {
                        _siDevices.Add(new SiDeviceInfo
                        {
                            // 1. [DB Key] 고유 관리 번호 (SEQ)
                            FixCodeNo = reader["FIX_CODENO"].ToString(),

                            // 2. [SI Config] CSV 생성을 위한 설정값들 매핑
                            ServerId = Convert.ToInt32(reader["SERVER_ID"]),
                            SystemCode = Convert.ToInt32(reader["SYSTEM_CODE"]),
                            DbDeviceId = Convert.ToInt32(reader["DEVICE_ID"]), // DB에 저장된 순서값

                            // 3. [Protocol Key] 스캔 전에는 모름 (0으로 초기화)
                            BacnetId = 0,

                            // 4. 기타 정보
                            CodeName = reader["CODE_NAME"].ToString(),
                            DeviceIp = reader["DEVICE_IP"].ToString(),
                            IsOnline = false
                        });
                    }

                    // UI 갱신
                    RefreshGrid();

                    // 결과 알림
                    string categoryName = selectedIndex == 2 ? "전력" : "설비";
                    MessageBox.Show($"[{categoryName}] DB 목록 로드 완료.\n총 {_siDevices.Count}대 (ParentID: {targetParentId})");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("DB 로드 에러: " + ex.Message);
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
        // [MainForm.cs]

        private async void btnStartScan_Click(object sender, EventArgs e)
        {
            // 중복 실행 방지
            if (_isScanning) return;

            if (cboSubnet.SelectedItem == null)
            {
                MessageBox.Show("스캔할 대역을 먼저 선택하세요.");
                return;
            }

            // 1. 선택된 대역 파싱 (예: "192.168.134.x (전력)" -> "192.168.134")
            string selectedText = cboSubnet.SelectedItem.ToString();
            string subnetPrefix = selectedText.Split('x')[0].Trim().TrimEnd('.');

            _isScanning = true;
            btnStartScan.Text = $"스캔 중 ({subnetPrefix})...";

            try
            {
                // 2. 스캔 시작 전 초기화 (기존 리스트의 상태 리셋)
                foreach (var d in _siDevices)
                {
                    d.IsOnline = false;
                    d.RealDeviceName = ""; // 스캔 전엔 실제 이름을 모름
                }

                await Task.Run(() => {
                    // 1~254 IP 리스트 생성
                    var ipList = Enumerable.Range(1, 254).Select(i => $"{subnetPrefix}.{i}").ToList();

                    // 3. 병렬 스캔 시작 (속도 향상)
                    Parallel.ForEach(ipList, new ParallelOptions { MaxDegreeOfParallelism = 50 }, (ip) => {

                        // ⭐ [Try-Catch 추가] 개별 장비 에러가 전체 프로그램을 멈추지 않게 함 (System.pdb 에러 방지)
                        try
                        {
                            // [통신] 장비 존재 여부 확인 (Who-Is)
                            var res = _inventoryService.DirectScanDevice(ip).Result;

                            if (res.DeviceId != 0xFFFFFFFF) // 장비 발견!
                            {
                                // [통신] 상세 정보 조회 (이름, 벤더 등)
                                var info = _inventoryService.GetDeviceInfo(res.Ip, res.DeviceId).Result;

                                string realName = info.ContainsKey("DeviceName") ? info["DeviceName"] : "Unknown";
                                string vendor = info.ContainsKey("Vendor") ? info["Vendor"] : "Unknown";

                                // 리스트 동기화 (Multi-thread 안전 장치)
                                lock (_siDevices)
                                {
                                    // IP 기준으로 기존(DB에 있는) 장비인지 확인
                                    var existDevice = _siDevices.FirstOrDefault(d => d.DeviceIp == ip);

                                    if (existDevice != null)
                                    {
                                        // [A] 매칭 성공 (기존 DB 장비)
                                        // -> DB에서 가져온 ServerId, SystemCode, DbDeviceId 등은 그대로 유지됨
                                        existDevice.IsOnline = true;
                                        existDevice.RealDeviceName = realName;

                                        // ⭐ 실제 통신 ID 업데이트 (DB의 DEVICE_ID와 구별됨)
                                        existDevice.BacnetId = res.DeviceId;

                                        _logger.Info($"[매칭] {ip} | Key:{existDevice.FixCodeNo} | ID:{existDevice.BacnetId}");
                                    }
                                    else
                                    {
                                        // [B] 신규 장비 (DB 미등록)
                                        // -> DB 정보가 없으므로 Export를 위해 '기본값'으로 초기화
                                        _siDevices.Add(new SiDeviceInfo
                                        {
                                            FixCodeNo = "NEW",          // 관리번호 없음
                                            BacnetId = res.DeviceId,    // 실제 통신 ID

                                            // ⭐ [추가] 신규 장비 기본값 (나중에 CSV 만들 때 필요)
                                            ServerId = 1,               // 기본 서버
                                            SystemCode = 0,             // 기본 시스템
                                            DbDeviceId = 0,             // 정렬 순서 (모름)

                                            DeviceIp = ip,
                                            CodeName = "(DB미등록)",
                                            RealDeviceName = realName,
                                            IsOnline = true
                                        });

                                        _logger.Warning($"[신규] {ip} (ID:{res.DeviceId}) | Real:{realName}");
                                    }
                                }
                            }
                        }
                        catch (Exception innerEx)
                        {
                            // 타임아웃 등 개별 장비 에러는 무시하고(로그만 남기고) 계속 진행
                            // _logger.Debug($"Skip {ip}: {innerEx.Message}");
                        }
                    });
                });

                // 4. UI 갱신 (Invoke 필수)
                this.Invoke(new Action(() => {
                    RefreshGrid(); // 그리드 초기화

                    // IP 순서대로 보기 좋게 정렬
                    var sorted = _siDevices.OrderBy(d =>
                    {
                        Version v;
                        return Version.TryParse(d.DeviceIp, out v) ? v : new Version("0.0.0.0");
                    }).ToList();

                    dataGridView1.DataSource = sorted;

                    // ⭐ [UI] 헤더 정리 (사용자 혼동 방지)
                    if (dataGridView1.Columns["FixCodeNo"] != null)
                        dataGridView1.Columns["FixCodeNo"].HeaderText = "DB Key (FIX_NO)";

                    if (dataGridView1.Columns["BacnetId"] != null)
                        dataGridView1.Columns["BacnetId"].HeaderText = "BACnet ID";

                    if (dataGridView1.Columns["DbDeviceId"] != null)
                        dataGridView1.Columns["DbDeviceId"].HeaderText = "정렬순서(ID)";

                    if (dataGridView1.Columns["RealDeviceName"] != null)
                        dataGridView1.Columns["RealDeviceName"].HeaderText = "실제 장비명 (Scan)";

                    // 불필요한 내부 컬럼 숨기기 (선택 사항)
                    if (dataGridView1.Columns["ServerId"] != null) dataGridView1.Columns["ServerId"].Visible = false;
                    if (dataGridView1.Columns["SystemCode"] != null) dataGridView1.Columns["SystemCode"].Visible = false;

                }));

                MessageBox.Show($"스캔 완료.\n총 목록: {_siDevices.Count}대 (Online: {_siDevices.Count(d => d.IsOnline)})");
            }
            catch (Exception ex)
            {
                _logger.Error("스캔 프로세스 오류", ex);
                MessageBox.Show("오류: " + ex.Message);
            }
            finally
            {
                _isScanning = false;
                btnStartScan.Text = "네트워크 스캔";
            }
        }

        // [MainForm.cs]

        // [MainForm.cs]

        private async void btnExport_Click(object sender, EventArgs e)
        {
            // 1. 저장될 폴더 경로
            string masterPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Exports", "SI_MASTER");

            // 2. 온라인 장비만 필터링
            var onlineDevices = _siDevices.FindAll(d => d.IsOnline);

            if (onlineDevices.Count == 0)
            {
                MessageBox.Show("온라인 장비가 없습니다. 먼저 스캔을 수행하세요.");
                return;
            }

            btnExport.Enabled = false;
            btnExport.Text = "델타 추출 중...";

            try
            {
                await Task.Run(async () => {
                    if (!Directory.Exists(masterPath)) Directory.CreateDirectory(masterPath);
                    var files = Directory.GetFiles(masterPath, "*.csv");

                    // 병렬 처리
                    Parallel.ForEach(onlineDevices, new ParallelOptions { MaxDegreeOfParallelism = 10 }, (dev) => {

                        // 신규 장비("NEW")는 마스터 파일이 없으므로 건너뜀
                        if (dev.FixCodeNo == "NEW") return;

                        // 마스터 파일 찾기
                        string foundMaster = files.FirstOrDefault(f => Path.GetFileName(f).Contains(dev.FixCodeNo));

                        if (!string.IsNullOrEmpty(foundMaster))
                        {
                            // 포인트 수집
                            var points = _inventoryService.HarvestPoints(dev.DeviceIp).Result;

                            if (points != null && points.Count > 0)
                            {
                                // ⭐ [수정] 서비스 메서드에 'DB 설정값' 전달
                                _inventoryService.ExportDeltaOnly(
                                    foundMaster,
                                    points,
                                    dev.ServerId,       // DB값: SERVER_ID
                                    dev.SystemCode,     // DB값: SYSTEM_CODE
                                    dev.DbDeviceId,     // DB값: DEVICE_ID (정렬순서)
                                    dev.FixCodeNo,      // DB값: DEVICE_SEQ
                                    (int)dev.BacnetId   // 통신값: BACnet ID (파일명 생성용)
                                );
                            }
                        }
                    });
                });

                MessageBox.Show("신규 포인트(Delta) 추출이 완료되었습니다.\nExports\\SI_DELTA_ONLY 폴더를 확인하세요.");

                // 결과 폴더 열기
                string deltaDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Exports", "SI_DELTA_ONLY");
                if (Directory.Exists(deltaDir))
                {
                    System.Diagnostics.Process.Start("explorer.exe", deltaDir);
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Export 중 오류 발생", ex);
                MessageBox.Show("오류: " + ex.Message);
            }
            finally
            {
                btnExport.Enabled = true;
                btnExport.Text = "SI 포인트 생성";
            }
        }
    }
}