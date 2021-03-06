using System;
using System.Collections.Generic;
using Branch.Packages.Contracts.Common.Branch;
using Microsoft.Extensions.Options;

namespace Branch.Apps.ServiceIdentity.Models
{
	public class XboxLiveIdentity : ICacheInfo
	{
		public string Gamertag { get; set; }

		public long XUID { get; set; }

		public DateTime CachedAt { get; set; }

		public Nullable<DateTime> ExpiresAt { get; set; }

		public bool IsFresh()
		{
			throw new NotImplementedException();
		}

		public bool IsFresh(DateTime date)
		{
			throw new NotImplementedException();
		}
	}
}
