using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;

namespace VsCodeMeadowUtil
{
	public class CommandResponse
	{
		public CommandResponse()
		{
		}

		[JsonProperty("id")]
		public string Id { get; set; } = null!;

		[JsonProperty("command")]
		public string Command { get; set; } = null!;

		[JsonProperty("error")]
		public string Error { get; set; } = null!;

		[JsonProperty("response")]
		public object Response { get; set; } = null!;
	}
}
