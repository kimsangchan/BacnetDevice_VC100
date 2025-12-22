using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using BacnetInventoryApp.Common;
using BacnetInventoryApp.Model;
using BacnetInventoryApp.Service;

namespace BacnetInventoryApp
{
    public partial class MainForm : Form
    {
        // ===== UI Controls (Designer 안 쓰고 코드로 생성) =====
        private Label lblTitle;
        private TextBox txtFacilityGroupId;
        private Button btnLoadDevices;
        private Label lblStatus;
        private DataGridView gridPoints;
        private DataGridView gridDevices;

        // ===== State =====
        private string _connectionString;          // Config/환경에서 로드 (여기서는 이미 있다고 가정)
        private List<FacilityDevice> _devices = new List<FacilityDevice>();
        private Button btnScanPoints;

        public MainForm()
        {
            InitializeComponent(); // ✅ Designer 없어도 이 메서드만 우리가 직접 구현하면 됨
            BuildUi();

            // 실데이터 예시: Server=192.168.131.127;Database=IBSInfo;Uid=sa;Pwd=admin;
            // 여기 부분은 너 프로젝트에 이미 있는 "Config.xml 기반" 로더를 그대로 쓰면 됨.
            // (지금 단계에선 보안 얘기 안 한다 했으니, 일단 동작 우선)
            _connectionString = LoadConnectionStringForNow();

            Logger.Info("[UI] MainForm ready");
        }


        private void BuildUi()
        {
            lblTitle = new Label
            {
                Text = "설비 그룹 MULTI_PARENT_ID (예: 4311744512)",
                Location = new Point(20, 20),
                AutoSize = true
            };

            txtFacilityGroupId = new TextBox
            {
                Text = "4311744512",
                Location = new Point(20, 45),
                Width = 240
            };

            btnLoadDevices = new Button
            {
                Text = "설비 디바이스 로드",
                Location = new Point(270, 43),
                Size = new Size(160, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(49, 130, 246),
                ForeColor = Color.White
            };
            btnLoadDevices.Click += btnLoadDevices_Click;
            btnScanPoints = new Button
            {
                Text = "선택 장비 포인트 스캔",
                Location = new Point(440, 43),
                Size = new Size(190, 32),
                FlatStyle = FlatStyle.Flat
            };
            btnScanPoints.Click += btnScanPoints_Click;
            this.Controls.Add(btnScanPoints);

            lblStatus = new Label
            {
                Text = "준비됨",
                Location = new Point(20, 85),
                AutoSize = true
            };
            gridPoints = new DataGridView
            {
                Location = new Point(20, 530),
                Width = this.ClientSize.Width - 40,
                Height = this.ClientSize.Height - 550,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,

                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AutoGenerateColumns = true,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
            };
            this.Controls.Add(gridPoints);

            gridDevices = new DataGridView
            {
                Location = new Point(20, 115),
                Width = this.ClientSize.Width - 40,
                Height = this.ClientSize.Height - 140,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,

                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AutoGenerateColumns = true,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
            };
            gridDevices.MultiSelect = true;
            gridDevices.SelectionMode = DataGridViewSelectionMode.FullRowSelect;

            this.Controls.Add(lblTitle);
            this.Controls.Add(txtFacilityGroupId);
            this.Controls.Add(btnLoadDevices);
            this.Controls.Add(lblStatus);
            this.Controls.Add(gridDevices);
        }
        private async void btnScanPoints_Click(object sender, EventArgs e)
        {
            try
            {
                if (_devices == null || _devices.Count == 0)
                {
                    lblStatus.Text = "먼저 설비 디바이스를 로드해.";
                    return;
                }

                // 1) Grid에서 선택된 FacilityDevice 추출
                var selected = new List<FacilityDevice>();
                foreach (DataGridViewRow row in gridDevices.SelectedRows)
                {
                    var item = row.DataBoundItem as FacilityDevice;
                    if (item != null) selected.Add(item);
                }

                if (selected.Count == 0)
                {
                    lblStatus.Text = "스캔할 장비를 선택해.";
                    return;
                }

                // 2) UI 잠금
                btnLoadDevices.Enabled = false;
                btnScanPoints.Enabled = false;

                Logger.Info("[UI] Point scan start selected=" + selected.Count);

                // 실데이터 흐름 예:
                // 선택 3대:
                // - SI=5  ip=172.16.130.100
                // - SI=6  ip=172.16.130.101
                // - SI=7  ip=172.16.130.103
                var prog = new Progress<string>(msg =>
                {
                    lblStatus.Text = msg;
                    Logger.Info("[UI][SCAN] " + msg);
                });

                // 3) 스캔 실행 (현업 기본 병렬=2~4)
                int maxParallel = 3;

                List<RawPointInfo> points;
                using (var svc = new BacnetInventoryApp.Service.BacnetPointScanService())
                {
                    points = await svc.ScanSelectedDevicesAsync(selected, maxParallel, prog, CancellationToken.None);
                }

                // 4) 결과 표시
                gridPoints.DataSource = null;
                gridPoints.DataSource = points;

                lblStatus.Text = "스캔 완료: 장비 " + selected.Count + "대, 포인트 " + points.Count + "개";
                Logger.Info("[UI] Point scan done devices=" + selected.Count + " points=" + points.Count);
            }
            catch (Exception ex)
            {
                lblStatus.Text = "스캔 실패: " + ex.Message;
                Logger.Error("[UI] Point scan failed", ex);
            }
            finally
            {
                btnLoadDevices.Enabled = true;
                btnScanPoints.Enabled = true;
            }
        }

        private void btnLoadDevices_Click(object sender, EventArgs e)
        {
            string groupId = (txtFacilityGroupId.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(groupId))
            {
                lblStatus.Text = "그룹 ID가 비었습니다.";
                Logger.Warn("[UI] groupId is empty");
                return;
            }

            Logger.Info("[UI] Load start group=" + groupId);
            lblStatus.Text = "로딩 중...";

            try
            {
                var svc = new FacilityDeviceService(_connectionString);
                _devices = svc.Load(groupId);

                // 실데이터 예시 표시:
                // _devices[0] = { DeviceId="20059", DeviceIp="172.16.130.98", DevicePort=47808, DeviceInfo="AHU-01" }
                gridDevices.DataSource = null;
                gridDevices.DataSource = _devices;

                lblStatus.Text = "완료: " + _devices.Count + "대";
                Logger.Info("[UI] Load done count=" + _devices.Count);
            }
            catch (Exception ex)
            {
                lblStatus.Text = "실패: " + ex.Message;
                Logger.Error("[UI] Load failed", ex);
            }
        }

        private string LoadConnectionStringForNow()
        {
            // 너 환경에서 이미 쓰는 방식으로 바꿔 끼우면 됨.
            // 지금은 "동작 확인"이 목적이라 하드코딩 형태로 둠.
            // 예: Server=192.168.131.127;Database=IBSInfo;Uid=sa;Pwd=admin;
            return "Server=192.168.131.127;Database=IBSInfo;User Id=sa;Password=admin123!@#;";
        }
    }
}
