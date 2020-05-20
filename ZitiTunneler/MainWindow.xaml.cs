﻿using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.IO;
using ZitiTunneler.Models;
using System.IO.Compression;

using ZitiTunneler.ServiceClient;
using System.ServiceProcess;
using System.Linq;
using System.Runtime.Remoting.Messaging;
using System.Diagnostics;
using System.Security.Principal;
using System.Net;

namespace ZitiTunneler {

	/// <summary>
	/// Interaction logic for MainWindow.xaml
	/// </summary>
	public partial class MainWindow:Window {

		public System.Windows.Forms.NotifyIcon notifyIcon;
		private DateTime _startDate;
		private System.Windows.Forms.Timer _timer;
		private Client serviceClient = null;
		private bool _isAttached = true;
		private int _right = 75;
		private int _bottom = 0;
		private double _maxHeight = 800d;

		private List<ZitiIdentity> identities {
			get {
				return (List<ZitiIdentity>)Application.Current.Properties["Identities"];
			}
		}

		public static bool IsAdministrator() {
			using (WindowsIdentity identity = WindowsIdentity.GetCurrent()) {
				WindowsPrincipal principal = new WindowsPrincipal(identity);
				return principal.IsInRole(WindowsBuiltInRole.Administrator);
			}
		}

		private void UpdateServiceFiles() {
			string[] files = Directory.GetFiles(Path.Combine(Environment.CurrentDirectory, "Service"));
			foreach (string file in files) {
				File.Delete(file);
				Console.WriteLine($"{file} is deleted.");
			}
			WebClient webClient = new WebClient();
			webClient.DownloadFile("https://actieve.com/windows-tunneler.zip", Path.Combine(Environment.CurrentDirectory, "Service")+@"\windows-tunneler.zip");
			ZipFile.ExtractToDirectory(Path.Combine(Environment.CurrentDirectory, "Service")+@"\windows-tunneler.zip", Path.Combine(Environment.CurrentDirectory, "Service"));
		}

		private void LaunchOrInstall() {
			ServiceController ctl = ServiceController.GetServices().FirstOrDefault(s => s.ServiceName=="ziti");
			if (ctl==null) {
				if (IsAdministrator()) {
					UpdateServiceFiles();
					ProcessStartInfo installService = new ProcessStartInfo();
					installService.CreateNoWindow=true;
					installService.UseShellExecute=false;
					installService.FileName=Path.Combine(Environment.CurrentDirectory, "Service")+@"\ziti-tunnel.exe";
					installService.WindowStyle=ProcessWindowStyle.Hidden;
					installService.Arguments="install";

					try {
						using (Process exeProcess = Process.Start(installService)) {
							exeProcess.WaitForExit();
							ctl=ServiceController.GetServices().FirstOrDefault(s => s.ServiceName=="ziti");
							if (ctl.Status!=ServiceControllerStatus.Running) {
								try {
									ctl.Start();
								} catch (Exception e) {
									SetCantDisplay();
								}
							}
						}
					} catch (Exception e) {
						MessageBox.Show(e.ToString());
					}
				} else MessageBox.Show("Not an admin");
			} else {
				if (ctl.Status!=ServiceControllerStatus.Running) {
					try {
						ctl.Start();
					} catch (Exception e) {
						SetCantDisplay();
					}
				}
			}
		}

		private List<ZitiService> services = new List<ZitiService>();
		public MainWindow() {
			InitializeComponent();

			App.Current.MainWindow.WindowState = WindowState.Normal;
			App.Current.MainWindow.Closing += MainWindow_Closing;
			notifyIcon = new System.Windows.Forms.NotifyIcon();
			notifyIcon.Visible = true;
			notifyIcon.Click += TargetNotifyIcon_Click;
			notifyIcon.Visible = true;

			LaunchOrInstall();
			SetNotifyIcon("white");
			InitializeComponent();
		}

		private void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
		{
			notifyIcon.Visible = false;
			notifyIcon.Icon.Dispose();
			notifyIcon.Dispose();
		}

		private void SetCantDisplay()
		{
			NoServiceView.Visibility = Visibility.Visible;
			SetNotifyIcon("red");
		}

		private void SetCanDisplay()
		{
			NoServiceView.Visibility = Visibility.Collapsed;
			SetNotifyIcon("green");
		}

		private void Window_MouseDown(object sender, MouseButtonEventArgs e) {
			if (!_isAttached&&e.ChangedButton == MouseButton.Left) this.DragMove();
		}

		private void Repaint() {
			LoadIdentities();
		}

		private void TargetNotifyIcon_Click(object sender, EventArgs e) {
			this.Show();
			this.Activate();
		}

		private void MainWindow1_Loaded(object sender, RoutedEventArgs e) {
			var desktopWorkingArea = System.Windows.SystemParameters.WorkArea;
			this.Left = desktopWorkingArea.Right - this.Width - _right;
			this.Top = desktopWorkingArea.Bottom - this.Height - _bottom;
			// add a new service client
			serviceClient = new Client();
			serviceClient.OnClientConnected += ServiceClient_OnClientConnected;
			serviceClient.OnClientDisconnected += ServiceClient_OnClientDisconnected;
			serviceClient.OnIdentityEvent += ServiceClient_OnIdentityEvent;
			serviceClient.OnMetricsEvent += ServiceClient_OnMetricsEvent;
			serviceClient.OnServiceEvent += ServiceClient_OnServiceEvent;
			serviceClient.OnTunnelStatusEvent += ServiceClient_OnTunnelStatusEvent;

			Application.Current.Properties.Add("ServiceClient", serviceClient);
			Application.Current.Properties.Add("Identities", new List<ZitiIdentity>());
			MainMenu.OnAttachmentChange += AttachmentChanged;

			try {
				serviceClient.Connect();
				//var s = serviceClient.GetStatus();
				//LoadStatusFromService(s.Status);
			} catch (Exception ex) {
				SetCantDisplay();
			}
			LoadIdentities();
			IdentityMenu.OnForgot += IdentityForgotten;
		}

		private void ServiceClient_OnClientConnected(object sender, object e) {
			this.Dispatcher.Invoke(() => {
				//e is _ALWAYS_ null at this time use this to display something if you want
				NoServiceView.Visibility = Visibility.Collapsed;
				SetNotifyIcon("white");
			});
		}

		private void ServiceClient_OnClientDisconnected(object sender, object e) {
			this.Dispatcher.Invoke(() => {
				SetCantDisplay();
			});
		}

		private void ServiceClient_OnIdentityEvent(object sender, IdentityEvent e) {
			if (e == null) return;

			ZitiIdentity zid = ZitiIdentity.FromClient(e.Id);
			Debug.WriteLine($"==== IdentityEvent    : action:{e.Action} fingerprint:{e.Id.FingerPrint} name:{e.Id.Name} ");

			this.Dispatcher.Invoke(() => {
				if (e.Action == "added") {
					var found = identities.Find(i => i.Fingerprint == e.Id.FingerPrint);
					if (found == null) {
						identities.Add(zid);
					}
				} else {
					IdentityForgotten(ZitiIdentity.FromClient(e.Id));
				}
				LoadIdentities();
			});
			MessageBox.Show($"IDENTITY EVENT. Action: {e.Action} fingerprint: {zid.Fingerprint}");
		}

		private void ServiceClient_OnMetricsEvent(object sender, List<Identity> ids) {
			if (ids != null) {
				long totalUp = 0;
				long totalDown = 0;
				foreach (var id in ids) {
					Debug.WriteLine($"==== MetricsEvent     : id {id.Name} down: {id.Metrics.Down} up:{id.Metrics.Up}");
					if (id?.Metrics != null) {
						totalDown += id.Metrics.Down;
						totalUp += id.Metrics.Up;
					}
				}
				this.Dispatcher.Invoke(() => {
					DownloadSpeed.Content = (totalDown / 1000).ToString();
					UploadSpeed.Content = (totalUp / 1000).ToString();
				});
			}
		}

		private void ServiceClient_OnServiceEvent(object sender, ServiceEvent e) {
			if (e == null) return;
			
			Debug.WriteLine($"==== ServiceEvent     : action:{e.Action} fingerprint:{e.Fingerprint} name:{e.Service.Name} ");
			this.Dispatcher.Invoke(() => {
				var found = identities.Find(id => id.Fingerprint == e.Fingerprint);

				if (found == null) {
					Debug.WriteLine($"{e.Action} service event for {e.Service.Name} but the provided identity fingerprint {e.Fingerprint} is not found!");
					return;
				}

				if (e.Action == "added") {
					ZitiService zs = new ZitiService(e.Service.Name, e.Service.HostName, e.Service.Port);
					found.Services.Add(zs);
				} else {
					found.Services.RemoveAll(s => s.Name == e.Service.Name);
				}
				LoadIdentities();
			});
		}

		private void ServiceClient_OnTunnelStatusEvent(object sender, TunnelStatusEvent e)
		{
			if (e == null) return; //just skip it for now...
			Debug.WriteLine($"==== TunnelStatusEvent: ");
			this.Dispatcher.Invoke(() =>
			{
				InitializeTimer((int)e.Status.Duration);
				LoadStatusFromService(e.Status);
				LoadIdentities();
			});
		}

		private void IdentityForgotten(ZitiIdentity forgotten) {
			ZitiIdentity idToRemove = null;
			foreach (var id in identities)
			{
				if(id.Fingerprint == forgotten.Fingerprint)
				{
					idToRemove = id;
					break;
				}
			}
			identities.Remove(idToRemove);
			LoadIdentities();
		}

		private void AttachmentChanged(bool attached) {
			_isAttached = attached;
			if (_isAttached) {
				Arrow.Visibility = Visibility.Visible;
				var desktopWorkingArea = System.Windows.SystemParameters.WorkArea;
				this.Left = desktopWorkingArea.Right-this.Width-_right;
				this.Top = desktopWorkingArea.Bottom-this.Height-_bottom;
			} else {
				Arrow.Visibility = Visibility.Collapsed;
				var desktopWorkingArea = System.Windows.SystemParameters.WorkArea;
				this.Left = desktopWorkingArea.Right-this.Width-75;
				this.Top = desktopWorkingArea.Bottom-this.Height-75;
			}
			MainMenu.Visibility = Visibility.Collapsed;
		}

		private void LoadStatusFromService(TunnelStatus status) {
			if (status != null) {
				NoServiceView.Visibility = Visibility.Collapsed;
				SetNotifyIcon("white");
				if (status.Active) {
					InitializeTimer((int)status.Duration);
					ConnectButton.Visibility = Visibility.Collapsed;
					DisconnectButton.Visibility = Visibility.Visible;
					SetNotifyIcon("green");
				} else {
					ConnectButton.Visibility = Visibility.Visible;
					DisconnectButton.Visibility = Visibility.Collapsed;
				}
				if (!Application.Current.Properties.Contains("ip"))
				{
					Application.Current.Properties.Add("ip", status?.IpInfo?.Ip);
				}
				if (!Application.Current.Properties.Contains("subnet"))
				{
					Application.Current.Properties.Add("subnet", status?.IpInfo?.Subnet);
				}
				if (!Application.Current.Properties.Contains("mtu"))
				{
					Application.Current.Properties.Add("mtu", status?.IpInfo?.MTU);
				}
				if (!Application.Current.Properties.Contains("dns"))
				{
					Application.Current.Properties.Add("dns", status?.IpInfo?.DNS);
				}

				foreach (var id in status.Identities)
				{
					updateViewWithIdentity(id);
				}
				LoadIdentities();
			} else {
				SetCantDisplay();
			}
		}

		private void updateViewWithIdentity(Identity id) {
			var zid = ZitiIdentity.FromClient(id);
			foreach (var i in identities)
			{
				if(i.Fingerprint == zid.Fingerprint)
				{
					identities.Remove(i);
					break;
				}
			}
			identities.Add(zid);
			LoadIdentities();
		}
		private void SetNotifyIcon(string iconPrefix) {
			System.IO.Stream iconStream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,/Assets/Images/ziti-"+iconPrefix+".ico")).Stream;
			notifyIcon.Icon = new System.Drawing.Icon(iconStream);
		}

		private void LoadIdentities() {
			IdList.Children.Clear();
			IdList.Height = 0;
			IdList.MaxHeight = _maxHeight-520;
			ZitiIdentity[] ids = identities.ToArray();
			this.Height = 460+(ids.Length*60);
			if (this.Height>_maxHeight) this.Height = _maxHeight;
			IdentityMenu.SetHeight(this.Height-160);
			for (int i=0; i<ids.Length; i++) {
				IdentityItem id = new IdentityItem();
				id.Identity = ids[i];
				id.OnClick += OpenIdentity;
				IdList.Children.Add(id);
				IdList.Height += 60;
			}
			var desktopWorkingArea = System.Windows.SystemParameters.WorkArea;
			this.Left = desktopWorkingArea.Right-this.Width-_right;
			this.Top = desktopWorkingArea.Bottom-this.Height-_bottom;
		}

		private void OpenIdentity(ZitiIdentity identity) {
			IdentityMenu.Identity = identity;
		}

		private void ShowMenu(object sender, MouseButtonEventArgs e) {
			MainMenu.Visibility = Visibility.Visible;
		}

		private void AddIdentity(object sender, MouseButtonEventArgs e) {
			Microsoft.Win32.OpenFileDialog jwtDialog = new Microsoft.Win32.OpenFileDialog();
			jwtDialog.DefaultExt = ".jwt";
			jwtDialog.Filter = "Ziti Identities (*.jwt)|*.jwt";
			if (jwtDialog.ShowDialog() == true) {
				string fileContent = File.ReadAllText(jwtDialog.FileName);
				
				try {
					Identity createdId = serviceClient.AddIdentity(System.IO.Path.GetFileName(jwtDialog.FileName), false, fileContent);
					ServiceClient.Client client = (ServiceClient.Client)Application.Current.Properties["ServiceClient"];
					client.IdentityOnOff(createdId.FingerPrint, true);
					if (createdId != null) {
						identities.Add(ZitiIdentity.FromClient(createdId));
						LoadIdentities();
						//MessageBox.Show("New identity added with fingerprint: " + createdId.FingerPrint);
						//updateViewWithIdentity(createdId);
					} else {
						// Jeremy buddy - error popup here
						MessageBox.Show("created id was null - wtf jeremy. your fault, um nope your fault clint, or probably Andrews");
					}
				} catch (ServiceException se) {
					MessageBox.Show(se.AdditionalInfo, se.Message);
				} catch (Exception ex) {
					MessageBox.Show("Unexpected error 2", ex.Message);
				}
				LoadIdentities();
			}
		}

		private void Connect(object sender, RoutedEventArgs e) {
			try {
				serviceClient.SetTunnelState(true);
				SetNotifyIcon("green");
				ConnectButton.Visibility = Visibility.Collapsed;
				DisconnectButton.Visibility = Visibility.Visible;
			} catch (ServiceException se) {
				ShowError(se.AdditionalInfo, se.Message);
			} catch (Exception ex) {
				ShowError("Unexpected Error", "Code 3:" + ex.Message);
			}
		}

		private void OnTimedEvent(object sender, EventArgs e) {
			TimeSpan span = (DateTime.Now - _startDate);
			int hours = span.Hours;
			int minutes = span.Minutes;
			int seconds = span.Seconds;
			var hoursString = (hours>9)?hours.ToString():"0"+hours;
			var minutesString = (minutes>9)? minutes.ToString():"0"+minutes;
			var secondsString = (seconds>9) ? seconds.ToString() : "0"+seconds;
			ConnectedTime.Content = hoursString+":"+minutesString+":"+secondsString;
		}

		private void InitializeTimer(int millisAgoStarted) {
			_startDate = DateTime.Now.Subtract(new TimeSpan(0,0,0,0, millisAgoStarted));
			_timer = new System.Windows.Forms.Timer();
			_timer.Interval = 100;
			_timer.Tick += OnTimedEvent;
			_timer.Enabled = true;
			_timer.Start();
		}
		private void Disconnect(object sender, RoutedEventArgs e) {
			try {
				ConnectedTime.Content =  "00:00:00";
				_timer.Stop();
				serviceClient.SetTunnelState(false);
				SetNotifyIcon("white");
				ConnectButton.Visibility = Visibility.Visible;
				DisconnectButton.Visibility = Visibility.Collapsed;
			} catch (ServiceException se) {
				ShowError(se.AdditionalInfo, se.Message);
			} catch (Exception ex) {
				ShowError("Unexpected Error", "Code 4:"+ex.Message);
			}
		}

		private void FormFadeOut_Completed(object sender, EventArgs e) {
			closeCompleted = true;
			//this.Close();
		}
		private bool closeCompleted = false;
		private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e) {
			if (!closeCompleted) {
				FormFadeOut.Begin();
				e.Cancel = true;
			}
		}

		private void ShowError(String title, String message) {
			ErrorTitle.Content = title;
			ErrorDetails.Text = message;
			ErrorView.Visibility = Visibility.Visible;
		}

		private void CloseError(object sender, MouseButtonEventArgs e) {
			ErrorView.Visibility = Visibility.Collapsed;
		}

		private void CloseApp(object sender, MouseButtonEventArgs e) {
			Application.Current.Shutdown();
		}
	}
}
