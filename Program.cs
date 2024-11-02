// See https://aka.ms/new-console-template for more information
using MonoTorrent.Client;
using MonoTorrent;
using System.Net;
using System.Diagnostics;
using System.Linq;
using System;
using MonoTorrent.Logging;
using System.Text;

Console.WriteLine("Hello, World!");


//	https://pleasuredome.github.io/pleasuredome/mame/

// use "merged" same as archive.org - parent and all clones in one ZIP


//string NagnetLink = "magnet:?xt=urn:btih:6aaf26520850348efa5afbe83f6783faac9c0985&dn=MAME%200.270%20ROMs%20%28merged%29&tr=udp%3A%2F%2Ftracker.opentrackr.org%3A1337%2Fannounce&tr=udp%3A%2F%2Fbt2.archive.org%3A6969%2Fannounce";
string magnetLink = "magnet:?xt=urn:btih:1cda40757c9257f41849c6d348064eba28aef1c7&dn=MAME%200.271%20ROMs%20%28merged%29&tr=udp%3A%2F%2Ftracker.opentrackr.org%3A1337%2Fannounce&tr=udp%3A%2F%2Fbt2.archive.org%3A6969%2Fannounce";


//Console.WriteLine($"name:{link.Name} size:{link.Size}");

//foreach (ITorrentManagerFile file in manager.Files)
//{
//	//Console.WriteLine($"path:{file.Path} fullpath:{file.FullPath} length:{file.Length} comp:{file.DownloadCompleteFullPath} incomp:{file.DownloadIncompleteFullPath}");
//}


CancellationTokenSource cancellation = new CancellationTokenSource();

var task = MainAsync(magnetLink, cancellation.Token);

Console.CancelKeyPress += delegate { cancellation.Cancel(); task.Wait(); };
AppDomain.CurrentDomain.ProcessExit += delegate { cancellation.Cancel(); task.Wait(); };

AppDomain.CurrentDomain.UnhandledException += delegate (object sender, UnhandledExceptionEventArgs e) { Console.WriteLine(e.ExceptionObject); cancellation.Cancel(); task.Wait(); };
Thread.GetDomain().UnhandledException += delegate (object sender, UnhandledExceptionEventArgs e) { Console.WriteLine(e.ExceptionObject); cancellation.Cancel(); task.Wait(); };

task.Wait();

static async Task MainAsync(string magnetLink, CancellationToken token)
{
	const int httpListeningPort = 55125;

	// Give an example of how settings can be modified for the engine.
	var settingBuilder = new EngineSettingsBuilder
	{
		// Allow the engine to automatically forward ports using upnp/nat-pmp (if a compatible router is available)
		AllowPortForwarding = false,

		// Automatically save a cache of the DHT table when all torrents are stopped.
		AutoSaveLoadDhtCache = true,

		// Automatically save 'FastResume' data when TorrentManager.StopAsync is invoked, automatically load it
		// before hash checking the torrent. Fast Resume data will be loaded as part of 'engine.AddAsync' if
		// torrent metadata is available. Otherwise, if a magnetlink is used to download a torrent, fast resume
		// data will be loaded after the metadata has been downloaded. 
		AutoSaveLoadFastResume = true,

		// If a MagnetLink is used to download a torrent, the engine will try to load a copy of the metadata
		// it's cache directory. Otherwise the metadata will be downloaded and stored in the cache directory
		// so it can be reloaded later.
		AutoSaveLoadMagnetLinkMetadata = true,

		// Use a fixed port to accept incoming connections from other peers for testing purposes. Production usages should use a random port, 0, if possible.
		ListenEndPoints = new Dictionary<string, IPEndPoint> {
					{ "ipv4", new IPEndPoint (IPAddress.Any, 55123) },
				//	{ "ipv6", new IPEndPoint (IPAddress.IPv6Any, 55123) }
				},

		// Use a fixed port for DHT communications for testing purposes. Production usages should use a random port, 0, if possible.
		DhtEndPoint = new IPEndPoint(IPAddress.Any, 55123),


		// Wildcards such as these are supported as long as the underlying .NET framework version, and the operating system, supports them:
		//HttpStreamingPrefix = $"http://+:{httpListeningPort}/"
		//HttpStreamingPrefix = $"http://*.mydomain.com:{httpListeningPort}/"

		// For now just bind to localhost.
		HttpStreamingPrefix = $"http://127.0.0.1:{httpListeningPort}/"

	
	};

	settingBuilder.ReportedListenEndPoints = new Dictionary<string, IPEndPoint> {
		{ "ipv4", new IPEndPoint( IPAddress.Parse("217.40.212.83"), 55123) }
	};


	using var engine = new ClientEngine(settingBuilder.ToSettings());

	Task task;

	MagnetLink link;

	MagnetLink.TryParse(magnetLink, out link);
	
	//task = new MagnetLinkStreaming(engine).DownloadAsync(link, token);
	
	task = new StandardDownloader(engine).DownloadAsync(link, token);

	if (engine.Settings.AllowPortForwarding)
		Console.WriteLine("uPnP or NAT-PMP port mappings will be created for any ports needed by MonoTorrent");

	try
	{
		await task;
	}
	catch (OperationCanceledException)
	{

	}

	foreach (var manager in engine.Torrents)
	{
		var stoppingTask = manager.StopAsync();
		while (manager.State != TorrentState.Stopped)
		{
			Console.WriteLine("{0} is {1}", manager.Torrent.Name, manager.State);
			await Task.WhenAll(stoppingTask, Task.Delay(250));
		}
		await stoppingTask;
		if (engine.Settings.AutoSaveLoadFastResume)
			Console.WriteLine($"FastResume data for {manager.Torrent?.Name ?? manager.InfoHashes.V1?.ToHex() ?? manager.InfoHashes.V2?.ToHex()} has been written to disk.");
	}

	if (engine.Settings.AutoSaveLoadDhtCache)
		Console.WriteLine($"DHT cache has been written to disk.");

	if (engine.Settings.AllowPortForwarding)
		Console.WriteLine("uPnP and NAT-PMP port mappings have been removed");
}

class StandardDownloader
{
	ClientEngine Engine { get; }
	Top10Listener Listener { get; }         // This is a subclass of TraceListener which remembers the last 20 statements sent to it

	public StandardDownloader(ClientEngine engine)
	{
		Engine = engine;
		Listener = new Top10Listener(10);
	}

	public async Task DownloadAsync(MagnetLink link, CancellationToken token)
	{
		// Torrents will be downloaded to this directory
		var downloadsPath = Path.Combine(Environment.CurrentDirectory, "Downloads");

		// .torrent files will be loaded from this directory (if any exist)
		var torrentsPath = Path.Combine(Environment.CurrentDirectory, "Torrents");

		//#if DEBUG
		//		LoggerFactory.Register(new TextWriterLogger(Console.Out));
		//#endif

		//// If the torrentsPath does not exist, we want to create it
		//if (!Directory.Exists(torrentsPath))
		//	Directory.CreateDirectory(torrentsPath);

		//// For each file in the torrents path that is a .torrent file, load it into the engine.
		//foreach (string file in Directory.GetFiles(torrentsPath))
		//{
		//	if (file.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase))
		//	{
		//		try
		//		{
		//			// EngineSettings.AutoSaveLoadFastResume is enabled, so any cached fast resume
		//			// data will be implicitly loaded. If fast resume data is found, the 'hash check'
		//			// phase of starting a torrent can be skipped.
		//			// 
		//			// TorrentSettingsBuilder can be used to modify the settings for this
		//			// torrent.
		//			var settingsBuilder = new TorrentSettingsBuilder
		//			{
		//				MaximumConnections = 60,
		//			};
		//			var manager = await Engine.AddAsync(file, downloadsPath, settingsBuilder.ToSettings());
		//			Console.WriteLine(manager.InfoHashes.V1OrV2.ToHex());
		//		}
		//		catch (Exception e)
		//		{
		//			Console.Write("Couldn't decode {0}: ", file);
		//			Console.WriteLine(e.Message);
		//		}
		//	}
		//}


		//Torrent torrent;


	

		var myManager = await Engine.AddStreamingAsync(link, "downloads");

		//Engine.AddAsync()



		// If we loaded no torrents, just exist. The user can put files in the torrents directory and start
		// the client again
		if (Engine.Torrents.Count == 0)
		{
			Console.WriteLine($"No torrents found in '{torrentsPath}'");
			Console.WriteLine("Exiting...");
			return;
		}


		// For each torrent manager we loaded and stored in our list, hook into the events
		// in the torrent manager and start the engine.
		foreach (TorrentManager manager in Engine.Torrents)
		{
			manager.PeersFound += (o, e) => {
				Listener.WriteLine(string.Format($"{e.GetType().Name}: {e.NewPeers} peers for {e.TorrentManager.Name}"));
			};
			manager.PeerConnected += (o, e) => {
				lock (Listener)
					Listener.WriteLine($"Connection succeeded: {e.Peer.Uri}");
			};
			manager.ConnectionAttemptFailed += (o, e) => {
				lock (Listener)
					Listener.WriteLine(
						$"Connection failed: {e.Peer.ConnectionUri} - {e.Reason}");
			};
			// Every time a piece is hashed, this is fired.
			manager.PieceHashed += delegate (object o, PieceHashedEventArgs e) {
				lock (Listener)
					Listener.WriteLine($"Piece Hashed: {e.PieceIndex} - {(e.HashPassed ? "Pass" : "Fail")}");
			};

			// Every time the state changes (Stopped -> Seeding -> Downloading -> Hashing) this is fired
			manager.TorrentStateChanged += delegate (object o, TorrentStateChangedEventArgs e) {
				lock (Listener)
					Listener.WriteLine($"OldState: {e.OldState} NewState: {e.NewState}");
			};

			// Every time the tracker's state changes, this is fired
			manager.TrackerManager.AnnounceComplete += (sender, e) => {
				Listener.WriteLine($"{e.Successful}: {e.Tracker}");
			};

			// Start the torrentmanager. The file will then hash (if required) and begin downloading/seeding.
			// As EngineSettings.AutoSaveLoadDhtCache is enabled, any cached data will be loaded into the
			// Dht engine when the first torrent is started, enabling it to bootstrap more rapidly.


			await manager.StartAsync();

			await manager.WaitForMetadataAsync(token);

			//string desiredFile = "yourfile.ext";
			//Torrent torrent = Torrent.Load("path/to/torrent/file");
			//for (int i = 0; i < torrent.Files.Length; i++)
			//{
			//	if (torrent.Files[i].Path.Contains(desiredFile))
			//	{
			//		torrent.Files[i].Priority = Priority.Highest;
			//	}
			//	else
			//	{
			//		torrent.Files[i].Priority = Priority.DoNotDownload;
			//	}
			//}


			//await rig.Manager.SetFilePriorityAsync(file, Priority.DoNotDownload);





			//manager.Files[0].Priority = Priority.Normal;


			foreach (ITorrentManagerFile file in manager.Files)
			{
				await manager.SetFilePriorityAsync(file, Priority.DoNotDownload);
				//file.Priority = Priority.DoNotDownload;
			
				//Console.WriteLine(file.Path);	// $"path:{file.Path} fullpath:{file.FullPath} length:{file.Length} comp:{file.DownloadCompleteFullPath} incomp:{file.DownloadIncompleteFullPath}");
			}

			Console.WriteLine("files set");

			var myFile = manager.Files.Where(f => f.Path == "mrdo.zip").Single();

			await manager.SetFilePriorityAsync(myFile, Priority.Normal);

			//manager.Files..Clear();

			//manager.Files.Add(myFile);




		}

		// While the torrents are still running, print out some stats to the screen.
		// Details for all the loaded torrent managers are shown.
		StringBuilder sb = new StringBuilder(1024);
		while (Engine.IsRunning)
		{
			sb.Remove(0, sb.Length);

			AppendFormat(sb, $"Transfer Rate:      {Engine.TotalDownloadRate / 1024.0:0.00}kB/sec ↓ / {Engine.TotalUploadRate / 1024.0:0.00}kB/sec ↑");
			AppendFormat(sb, $"Memory Cache:       {Engine.DiskManager.CacheBytesUsed / 1024.0:0.00}/{Engine.Settings.DiskCacheBytes / 1024.0:0.00} kB");
			AppendFormat(sb, $"Disk IO Rate:       {Engine.DiskManager.ReadRate / 1024.0:0.00} kB/s read / {Engine.DiskManager.WriteRate / 1024.0:0.00} kB/s write");
			AppendFormat(sb, $"Disk IO Total:      {Engine.DiskManager.TotalBytesRead / 1024.0:0.00} kB read / {Engine.DiskManager.TotalBytesWritten / 1024.0:0.00} kB written");
			AppendFormat(sb, $"Open Files:         {Engine.DiskManager.OpenFiles} / {Engine.DiskManager.MaximumOpenFiles}");
			AppendFormat(sb, $"Open Connections:   {Engine.ConnectionManager.OpenConnections}");
			AppendFormat(sb, $"DHT State:          {Engine.Dht.State}");

			// Print out the port mappings
			foreach (var mapping in Engine.PortMappings.Created)
				AppendFormat(sb, $"Successful Mapping    {mapping.PublicPort}:{mapping.PrivatePort} ({mapping.Protocol})");
			foreach (var mapping in Engine.PortMappings.Failed)
				AppendFormat(sb, $"Failed mapping:       {mapping.PublicPort}:{mapping.PrivatePort} ({mapping.Protocol})");
			foreach (var mapping in Engine.PortMappings.Pending)
				AppendFormat(sb, $"Pending mapping:      {mapping.PublicPort}:{mapping.PrivatePort} ({mapping.Protocol})");

			foreach (TorrentManager manager in Engine.Torrents)
			{
				AppendSeparator(sb);
				AppendFormat(sb, $"State:              {manager.State}");
				AppendFormat(sb, $"Name:               {(manager.Torrent == null ? "MetaDataMode" : manager.Torrent.Name)}");
				AppendFormat(sb, $"Progress:           {manager.Progress:0.00}");
				AppendFormat(sb, $"Transferred:        {manager.Monitor.DataBytesReceived / 1024.0 / 1024.0:0.00} MB ↓ / {manager.Monitor.DataBytesSent / 1024.0 / 1024.0:0.00} MB ↑");
				AppendFormat(sb, $"Tracker Status");
				foreach (var tier in manager.TrackerManager.Tiers)
					AppendFormat(sb, $"\t{tier.ActiveTracker} : Announce Succeeded: {tier.LastAnnounceSucceeded}. Scrape Succeeded: {tier.LastScrapeSucceeded}.");

				if (manager.PieceManager != null)
					AppendFormat(sb, "Current Requests:   {0}", await manager.PieceManager.CurrentRequestCountAsync());

				var peers = await manager.GetPeersAsync();
				AppendFormat(sb, "Outgoing:");
				foreach (PeerId p in peers.Where(t => t.ConnectionDirection == Direction.Outgoing))
				{
					AppendFormat(sb, $"\t{p.AmRequestingPiecesCount} - {(p.Monitor.DownloadRate / 1024.0):0.00}/{(p.Monitor.UploadRate / 1024.0):0.00}kB/sec - {p.Uri} - {p.EncryptionType}");
				}
				AppendFormat(sb, "");
				AppendFormat(sb, "Incoming:");
				foreach (PeerId p in peers.Where(t => t.ConnectionDirection == Direction.Incoming))
				{
					AppendFormat(sb, $"\t{p.AmRequestingPiecesCount} - {(p.Monitor.DownloadRate / 1024.0):0.00}/{(p.Monitor.UploadRate / 1024.0):0.00}kB/sec - {p.Uri} - {p.EncryptionType}");
				}

				AppendFormat(sb, "", null);
				if (manager.Torrent != null)
					foreach (var file in manager.Files)
						AppendFormat(sb, "{1:0.00}% - {0}", file.Path, file.BitField.PercentComplete);
			}
			Console.Clear();
			Console.WriteLine(sb.ToString());
			Listener.ExportTo(Console.Out);

			await Task.Delay(5000, token);
		}
	}

	void Manager_PeersFound(object sender, PeersAddedEventArgs e)
	{
		lock (Listener)
			Listener.WriteLine($"Found {e.NewPeers} new peers and {e.ExistingPeers} existing peers");//throw new Exception("The method or operation is not implemented.");
	}

	void AppendSeparator(StringBuilder sb)
	{
		AppendFormat(sb, "");
		AppendFormat(sb, "- - - - - - - - - - - - - - - - - - - - - - - - - - - - - -");
		AppendFormat(sb, "");
	}

	void AppendFormat(StringBuilder sb, string str, params object[] formatting)
	{
		if (formatting != null && formatting.Length > 0)
			sb.AppendFormat(str, formatting);
		else
			sb.Append(str);
		sb.AppendLine();
	}
}


public class Top10Listener : TraceListener
{
	private readonly int capacity;
	private readonly LinkedList<string> traces;

	public Top10Listener(int capacity)
	{
		this.capacity = capacity;
		this.traces = new LinkedList<string>();
	}

	public override void Write(string message)
	{
		lock (traces)
			traces.Last.Value += message;
	}

	public override void WriteLine(string message)
	{
		lock (traces)
		{
			if (traces.Count >= capacity)
				traces.RemoveFirst();

			traces.AddLast(message);
		}
	}

	public void ExportTo(TextWriter output)
	{
		lock (traces)
			foreach (string s in this.traces)
				output.WriteLine(s);
	}
}

public class TextWriterLogger : IRootLogger
{
	TextWriter Writer { get; }

	public TextWriterLogger(TextWriter writer)
		=> Writer = writer;

	public void Debug(string prefix, string message)
	{
		Writer?.WriteLine($"DEBUG:{prefix}:{message}");
	}

	public void Error(string prefix, string message)
	{
		Writer?.WriteLine($"ERROR:{prefix}:{message}");
	}

	public void Info(string prefix, string message)
	{
		Writer?.WriteLine($"INFO: {prefix}:{message}");
	}
}

public interface IRootLogger
{
	void Info(string name, string message);
	void Debug(string name, string message);
	void Error(string name, string message);
}