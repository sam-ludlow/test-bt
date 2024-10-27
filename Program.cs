// See https://aka.ms/new-console-template for more information
using MonoTorrent.Client;
using MonoTorrent;
using System.Net;
using System.Diagnostics;

Console.WriteLine("Hello, World!");


//	https://pleasuredome.github.io/pleasuredome/mame/

// use "merged" same as archive.org - parent and all clones in one ZIP


string magnetLink = "magnet:?xt=urn:btih:6aaf26520850348efa5afbe83f6783faac9c0985&dn=MAME%200.270%20ROMs%20%28merged%29&tr=udp%3A%2F%2Ftracker.opentrackr.org%3A1337%2Fannounce&tr=udp%3A%2F%2Fbt2.archive.org%3A6969%2Fannounce";


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
	
	task = new MagnetLinkStreaming(engine).DownloadAsync(link, token);
	
	//task = new StandardDownloader(engine).DownloadAsync(token);

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




class MagnetLinkStreaming
{
	ClientEngine Engine { get; }

	public MagnetLinkStreaming(ClientEngine engine)
	{
		Engine = engine;
	}

	public async Task DownloadAsync(MagnetLink link, CancellationToken token)
	{
		var times = new List<(string message, TimeSpan time)>();
		var manager = await Engine.AddStreamingAsync(link, "downloads");

		var overall = Stopwatch.StartNew();
		var firstPeerFound = Stopwatch.StartNew();
		var firstPeerConnected = Stopwatch.StartNew();
		manager.PeerConnected += (o, e) => {
			if (!firstPeerConnected.IsRunning)
				return;

			firstPeerConnected.Stop();
			lock (times)
				times.Add(("First peer connected. Time since torrent started: ", firstPeerConnected.Elapsed));
		};
		manager.PeersFound += (o, e) => {
			if (!firstPeerFound.IsRunning)
				return;

			firstPeerFound.Stop();
			lock (times)
				times.Add(($"First peers found via {e.GetType().Name}. Time since torrent started: ", firstPeerFound.Elapsed));
		};
		manager.PieceHashed += (o, e) => {
			if (manager.State != TorrentState.Downloading)
				return;

			lock (times)
				times.Add(($"Piece {e.PieceIndex} hashed. Time since torrent started: ", overall.Elapsed));
		};

		await manager.StartAsync();
		await manager.WaitForMetadataAsync(token);

		var largestFile = manager.Files.OrderByDescending(t => t.Length).First();
		var stream = await manager.StreamProvider.CreateStreamAsync(largestFile, false);

		// Read the middle
		await TimedRead(manager, stream, stream.Length / 2, times, token);
		// Then the start
		await TimedRead(manager, stream, 0, times, token);
		// Then the last piece
		await TimedRead(manager, stream, stream.Length - 2, times, token);
		// Then the 3rd last piece
		await TimedRead(manager, stream, stream.Length - manager.Torrent.PieceLength * 3, times, token);
		// Then the 5th piece
		await TimedRead(manager, stream, manager.Torrent.PieceLength * 5, times, token);
		// Then 1/3 of the way in
		await TimedRead(manager, stream, stream.Length / 3, times, token);
		// Then 2/3 of the way in
		await TimedRead(manager, stream, stream.Length / 3 * 2, times, token);
		// Then 1/5 of the way in
		await TimedRead(manager, stream, stream.Length / 5, times, token);
		// Then 4/5 of the way in
		await TimedRead(manager, stream, stream.Length / 5 * 4, times, token);

		lock (times)
		{
			foreach (var (message, time) in times)
				Console.WriteLine($"{message} {time.TotalSeconds:0.00} seconds");
		}

		await manager.StopAsync();
	}

	async Task TimedRead(TorrentManager manager, Stream stream, long position, List<(string, TimeSpan)> times, CancellationToken token)
	{
		var stopwatch = Stopwatch.StartNew();
		stream.Seek(position, SeekOrigin.Begin);
		await stream.ReadAsync(new byte[1], 0, 1, token);
		lock (times)
			times.Add(($"Read piece: {manager.Torrent.ByteOffsetToPieceIndex(stream.Position - 1)}. Time since seeking: ", stopwatch.Elapsed));
	}
}