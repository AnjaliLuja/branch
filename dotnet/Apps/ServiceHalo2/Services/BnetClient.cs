using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Amazon;
using Amazon.SQS;
using Amazon.SQS.Model;
using Branch.Apps.ServiceHalo2.Database;
using Branch.Apps.ServiceHalo2.Models;
using Branch.Clients.Json;
using Branch.Clients.S3;
using Branch.Clients.Sqs;
using Branch.Packages.Bae;
using Branch.Packages.Contracts.Common.Branch;
using Branch.Packages.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using PuppeteerSharp;
using Sentry;

namespace Branch.Apps.ServiceHalo2.Services
{
	public class BnetClient : IDisposable
	{
		private Browser _browser;
		private readonly S3Client _s3Client;
		private readonly SqsClient _sqsClient;
		private readonly string _puppeteerRemoteHost;
		private readonly ILogger _logger;
		private readonly IHub _sentry;
		private readonly IServiceProvider _serviceProvider;
		private const string _serviceRecordUrl = "https://halo.bungie.net/Stats/PlayerStatsHalo2.aspx?player={0}";
		private const string _serviceRecordHeaderRegex = @"Total Games: ([0-9]+)|Last Played: ([0-9\/ :]+(?:AM|PM))|Total Kills: ([0-9]+)|Total Deaths: ([0-9]+)|Total Assists: ([0-9]+)";

		public BnetClient(IOptions<Config> config, S3Client s3Client, SqsClient sqsClient, ILoggerFactory loggerFactory, IHub sentry, IServiceProvider serviceProvider)
		{
			_s3Client = s3Client;
			_sqsClient = sqsClient;
			_logger = loggerFactory.CreateLogger(nameof(BnetClient));
			_sentry = sentry;
			_serviceProvider = serviceProvider;
			_puppeteerRemoteHost = config.Value.PuppeteerRemoteHost;

			this.ConnectOrLaunch().Wait();
		}

		public async Task ConnectOrLaunch()
		{
			if (_puppeteerRemoteHost != null)
			{
				var parts = _puppeteerRemoteHost.Split(":");
				var lookups = await Dns.GetHostAddressesAsync(parts[0]);
				var ip = lookups.First().ToString();
				var puppeteerUrl = $"http://{ip}:{parts[1]}";
				var jsonClient = new JsonClient(puppeteerUrl);

				_logger.LogInformation($"Fetching websocket url from {puppeteerUrl}");

				var version = await jsonClient.Do<Dictionary<string, string>>("GET", "/json/version");
				var url = version["webSocketDebuggerUrl"];

				_logger.LogInformation($"Connecting to browser with websocker url {url}");

				_browser = await Puppeteer.ConnectAsync(new ConnectOptions { BrowserWSEndpoint = url });

				_logger.LogInformation("Connected to browser");

				return;
			}

			_logger.LogInformation("Downloading browser");

			await new BrowserFetcher().DownloadAsync(BrowserFetcher.DefaultRevision);

			_logger.LogInformation("Launching headless browser");

			_browser = await Puppeteer.LaunchAsync(new LaunchOptions { Headless = true });

			_logger.LogInformation("Launched headless browser");
		}

		public async Task<bool> CacheServiceRecord(string gamertag)
		{
			var dbClient = _serviceProvider.GetOrActivateService<DatabaseClient>();

			// Don't cache again
			var escapedGt = gamertag.ToSlug();
			var cacheMetaIdent = $"sr-{escapedGt}";
			var srInfo = await dbClient.GetCacheMeta(cacheMetaIdent);
			if (srInfo?.CacheState != "in_progress")
				return true;

			using (_logger.BeginScope($"Caching Halo 2 Service Record for {gamertag}"))
			using (var page = await _browser.NewPageAsync())
			{
				try
				{
					var url = string.Format(_serviceRecordUrl, gamertag);
					await page.GoToAsync(url);

					_logger.LogInformation($"Checking if {gamertag} has played Halo 2");

					var playerFound = await page.EvaluateFunctionAsync<bool>(@"
						() => !window.document.querySelector('.rgNoRecords')
					");

					if (!playerFound)
					{
						// TODO(0xdeafcafe): Record player never played
						_logger.LogInformation($"{gamertag} never played");
						await dbClient.SetCacheMeta(cacheMetaIdent, "failed", new BaeException("player_not_found"));

						return true;
					}

					var pageInfo = await page.EvaluateFunctionAsync<Dictionary<string, string>>(@"
						() => {
							const sr = window.document.querySelector('#ctl00_mainContent_bnetpgl_identityStrip_div1');
							const gamertag = sr.querySelector('.header_stats > ul > li > h3').innerText;
							const emblemUrl = sr.querySelector('.profile_picA > img').src;
							const clanInfo = sr.querySelector('.firstline > a').innerText;
							const playerInfo = sr.querySelector('.secondline').innerText;

							return {
								gamertag,
								emblemUrl,
								clanInfo,
								playerInfo,
							};
						}
					");

					var sr = Regex.Matches(pageInfo["playerInfo"], _serviceRecordHeaderRegex, RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase);
					if (sr.Count != 5)
					{
						var ex = new FormatException("service record regex failed");
						ex.Data.Add("PlayerInfo", pageInfo["playerInfo"]);

						_logger.LogError(ex, "Unable to parse Service Record");
						_sentry.CaptureException(ex);

						return false;
					}

					var matchPageCount = await page.EvaluateFunctionAsync<int>(@"
						() => window.document.querySelector('.rgArrPart2 > a:nth-child(2)').href.split('=').slice(-1).pop()
					");
					_logger.LogInformation($"Recent match page count for {gamertag} is {matchPageCount}");
					await queueRecentMatchPages(gamertag, matchPageCount);

					var serviceRecord = new ServiceRecord
					{
						Gamertag = pageInfo["gamertag"],
						EmblemUrl = pageInfo["emblemUrl"],
						ClanName = pageInfo["clanInfo"],
						TotalGames = int.Parse(sr[0].Groups[1].Value),
						LastPlayed = DateTime.Parse(sr[1].Groups[2].Value),
						TotalKills = int.Parse(sr[2].Groups[3].Value),
						TotalDeaths = int.Parse(sr[3].Groups[4].Value),
						TotalAssists = int.Parse(sr[4].Groups[5].Value),
					};

					await dbClient.SetCacheMeta(cacheMetaIdent, "complete");
					await dbClient.SetServiceRecord(serviceRecord);

					return true;
				}
				finally
				{
					await page.CloseAsync();
				}
			}
		}

		private async Task queueRecentMatchPages(string gamertag, int pages)
		{
			_logger.LogInformation($"Queueing {gamertag}'s recent matches");

			var queueMessages = new QueueEvent[pages];
			for (var i = 0; i < pages; i++)
			{
				queueMessages[i] = new QueueEvent
				{
					Type = QueueEventTypes.CacheRecentMatches,
					Payload = new MatchHistoryPayload
					{
						Gamertag = gamertag,
						Page = i + 1,
					},
				};
			}

			var tasks = new List<Task>();
			var chunkyChunks = queueMessages.Split(10);
			foreach (var chunk in chunkyChunks)
				tasks.Add(_sqsClient.SendMessageBatchAsync(chunk));

			_logger.LogInformation($"{gamertag} has {pages} pages of recent matches over {tasks.Count} tasks");

			await Task.WhenAll(tasks.ToArray());
		}

		public void Dispose()
		{
			((IDisposable)_browser)?.Dispose();
		}
	}
}
