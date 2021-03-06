﻿#region Imports

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Configuration;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Serialization;

#endregion Imports

namespace IPBan
{
    /// <summary>
    /// Configuration for ip ban app
    /// </summary>
    public class IPBanConfig
    {
        private readonly Dictionary<string, string> appSettings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private ExpressionsToBlock expressions;
        private Regex whiteListRegex;
        private Regex blackListRegex;

        private readonly LogFileToParse[] logFiles;
        private readonly TimeSpan banTime = TimeSpan.FromDays(1.0d);
        private readonly TimeSpan expireTime = TimeSpan.FromDays(1.0d);
        private readonly TimeSpan cycleTime = TimeSpan.FromMinutes(1.0d);
        private readonly TimeSpan minimumTimeBetweenFailedLoginAttempts = TimeSpan.FromSeconds(5.0);
        private readonly int failedLoginAttemptsBeforeBan = 5;
        private readonly string firewallRulePrefix = "IPBan_";
        private readonly HashSet<string> blackList = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> whiteList = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly bool clearBannedIPAddressesOnRestart;
        private readonly HashSet<string> userNameWhitelist = new HashSet<string>(StringComparer.Ordinal);
        private readonly bool createWhitelistFirewallRule;
        private readonly int userNameWhitelistMaximumEditDistance = 2;
        private readonly int failedLoginAttemptsBeforeBanUserNameWhitelist = 20;
        private readonly Dictionary<string, string> osAndFirewallType = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly string processToRunOnBan;
        private readonly string getUrlUpdate;
        private readonly string getUrlStart;
        private readonly string getUrlStop;
        private readonly string getUrlConfig;
        private readonly string externalIPAddressUrl;
        private readonly IDnsLookup dns;

        private IPBanConfig(string xml, IDnsLookup dns)
        {
            this.dns = dns;

            // deserialize with XmlDocument, the .net core Configuration class is quite buggy
            XmlDocument doc = new XmlDocument();
            doc.LoadXml(xml);
            foreach (XmlNode node in doc.SelectNodes("//appSettings/add"))
            {
                appSettings[node.Attributes["key"].Value] = node.Attributes["value"].Value;
            }

            GetConfig<int>("FailedLoginAttemptsBeforeBan", ref failedLoginAttemptsBeforeBan);
            GetConfig<TimeSpan>("BanTime", ref banTime);
            GetConfig<bool>("ClearBannedIPAddressesOnRestart", ref clearBannedIPAddressesOnRestart);
            GetConfig<TimeSpan>("ExpireTime", ref expireTime);
            GetConfig<TimeSpan>("CycleTime", ref cycleTime);
            GetConfig<TimeSpan>("MinimumTimeBetweenFailedLoginAttempts", ref minimumTimeBetweenFailedLoginAttempts);
            GetConfig<string>("FirewallRulePrefix", ref firewallRulePrefix);
            GetConfig<bool>("CreateWhitelistFirewallRule", ref createWhitelistFirewallRule);

            string whiteListString = GetConfig<string>("Whitelist", string.Empty);
            string whiteListRegexString = GetConfig<string>("WhitelistRegex", string.Empty);
            string blacklistString = GetConfig<string>("Blacklist", string.Empty);
            string blacklistRegexString = GetConfig<string>("BlacklistRegex", string.Empty);
            PopulateList(whiteList, ref whiteListRegex, whiteListString, whiteListRegexString);
            PopulateList(blackList, ref blackListRegex, blacklistString, blacklistRegexString);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                expressions = new XmlSerializer(typeof(ExpressionsToBlock)).Deserialize(new XmlNodeReader(doc.SelectSingleNode("//ExpressionsToBlock"))) as ExpressionsToBlock;
                if (expressions != null)
                {
                    foreach (ExpressionsToBlockGroup group in expressions.Groups)
                    {
                        foreach (ExpressionToBlock expression in group.Expressions)
                        {
                            expression.Regex = (expression.Regex ?? string.Empty).Trim();
                            if (expression.Regex.Length != 0)
                            {
                                if (expression.Regex[0] == '^')
                                {
                                    expression.Regex = "^\\s*?" + expression.Regex.Substring(1) + "\\s*?";
                                }
                                else
                                {
                                    expression.Regex = "\\s*?" + expression.Regex + "\\s*?";
                                }
                            }
                        }
                    }
                }
            }
            else
            {
                expressions = new ExpressionsToBlock { Groups = new ExpressionsToBlockGroup[0] };
            }
            try
            {
                LogFilesToParse logFilesToParse = new XmlSerializer(typeof(LogFilesToParse)).Deserialize(new XmlNodeReader(doc.SelectSingleNode("//LogFilesToParse"))) as LogFilesToParse;
                logFiles = (logFilesToParse == null ? new LogFileToParse[0] : logFilesToParse.LogFiles);
            }
            catch (Exception ex)
            {
                IPBanLog.Error(ex);
                logFiles = new LogFileToParse[0];
            }
            GetConfig<string>("ProcessToRunOnBan", ref processToRunOnBan);

            // retrieve firewall configuration
            string[] firewallTypes = GetConfig<string>("FirewallType", string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries);
            foreach (string firewallOSAndType in firewallTypes)
            {
                string[] pieces = firewallOSAndType.Split(':');
                if (pieces.Length == 2)
                {
                    osAndFirewallType[pieces[0]] = pieces[1];
                }
            }

            string userNameWhiteListString = GetConfig<string>("UserNameWhiteList", string.Empty);
            foreach (string userName in userNameWhiteListString.Split(','))
            {
                string userNameTrimmed = userName.Normalize().Trim();
                if (userNameTrimmed.Length > 0)
                {
                    userNameWhitelist.Add(userNameTrimmed);
                }
            }
            GetConfig<int>("UserNameWhiteListMinimumEditDistance", ref userNameWhitelistMaximumEditDistance);
            GetConfig<int>("FailedLoginAttemptsBeforeBanUserNameWhitelist", ref failedLoginAttemptsBeforeBanUserNameWhitelist);
            GetConfig<string>("GetUrlUpdate", ref getUrlUpdate);
            GetConfig<string>("GetUrlStart", ref getUrlStart);
            GetConfig<string>("GetUrlStop", ref getUrlStop);
            GetConfig<string>("GetUrlConfig", ref getUrlConfig);
            GetConfig<string>("ExternalIPAddressUrl", ref externalIPAddressUrl);
        }

        private void PopulateList(HashSet<string> set, ref Regex regex, string setValue, string regexValue)
        {
            setValue = (setValue ?? string.Empty).Trim();
            regexValue = (regexValue ?? string.Empty).Replace("*", @"[0-9A-Fa-f]+?").Trim();
            set.Clear();
            regex = null;

            if (!string.IsNullOrWhiteSpace(setValue))
            {
                foreach (string v in setValue.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string ipOrDns = v.Trim();
                    if (ipOrDns != "0.0.0.0" && ipOrDns != "::0" && ipOrDns != "127.0.0.1" && ipOrDns != "::1")
                    {
                        try
                        {
                            if (IPAddressRange.TryParse(ipOrDns, out _))
                            {
                                set.Add(ipOrDns);
                            }
                            else
                            {
                                IPAddress[] addresses = dns.GetHostEntryAsync(ipOrDns).Sync().AddressList;
                                if (addresses != null)
                                {
                                    foreach (IPAddress adr in addresses)
                                    {
                                        set.Add(adr.ToString());
                                    }
                                }
                            }
                        }
                        catch (System.Net.Sockets.SocketException)
                        {
                            // ignore, dns lookup fails
                        }
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(regexValue))
            {
                regex = ParseRegex(regexValue);
            }
        }

        /// <summary>
        /// Get a regex from text - this handles multi-line regex using \n, combining it into a single line
        /// To find newlines in the text, use the escape code for \n using \u0010
        /// </summary>
        /// <param name="text">Text</param>
        /// <returns>Regex</returns>
        public static Regex ParseRegex(string text)
        {
            string[] lines = text.Split('\n');
            StringBuilder sb = new StringBuilder();
            foreach (string line in lines)
            {
                string trimmedLine = line.Trim();
                if (trimmedLine.Length != 0)
                {
                    sb.Append(trimmedLine);
                }
            }
            return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        /// <summary>
        /// Get a value from configuration manager app settings
        /// </summary>
        /// <typeparam name="T">Type of value to get</typeparam>
        /// <param name="key">Key</param>
        /// <param name="defaultValue">Default value if null or not found</param>
        /// <returns>Value</returns>
        public T GetConfig<T>(string key, T defaultValue = default)
        {
            try
            {
                var converter = TypeDescriptor.GetConverter(typeof(T));
                return (T)converter.ConvertFromInvariantString(appSettings[key]);
            }
            catch
            {
                return defaultValue;
            }
        }

        /// <summary>
        /// Set a field / variable from configuration manager app settings. If null or not found, nothing is changed.
        /// </summary>
        /// <typeparam name="T">Type of value to set</typeparam>
        /// <param name="key">Key</param>
        /// <param name="value">Value</param>
        public void GetConfig<T>(string key, ref T value)
        {
            try
            {
                var converter = TypeDescriptor.GetConverter(typeof(T));
                value = (T)converter.ConvertFromInvariantString(appSettings[key]);
            }
            catch
            {
            }
        }

        /// <summary>
        /// Load IPBan config from file
        /// </summary>
        /// <param name="configFilePath">Config file path</param>
        /// <param name="service">Service</param>
        /// <param name="dns">Dns lookup for resolving ip addresses</param>
        /// <returns>IPBanConfig</returns>
        public static IPBanConfig LoadFromFile(string configFilePath, IDnsLookup dns)
        {
            configFilePath = (File.Exists(configFilePath) ? configFilePath : ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None).FilePath);
            if (!File.Exists(configFilePath))
            {
                throw new FileNotFoundException("Unable to find config file " + configFilePath);
            }
            return LoadFromXml(File.ReadAllText(configFilePath), dns);
        }

        /// <summary>
        /// Load IPBan config from XML
        /// </summary>
        /// <param name="xml">XML string</param>
        /// <param name="service">Service</param>
        /// <param name="dns">Dns lookup for resolving ip addresses</param>
        /// <returns>IPBanConfig</returns>
        public static IPBanConfig LoadFromXml(string xml, IDnsLookup dns)
        {
            return new IPBanConfig(xml, dns);
        }

        /// <summary>
        /// Check if an ip address is whitelisted
        /// </summary>
        /// <param name="ipAddress">IP Address</param>
        /// <returns>True if whitelisted, false otherwise</returns>
        public bool IsWhitelisted(string ipAddress)
        {
            return !string.IsNullOrWhiteSpace(ipAddress) &&
                (whiteList.Contains(ipAddress) ||
                !IPAddress.TryParse(ipAddress, out IPAddress ip) ||
                (whiteListRegex != null && whiteListRegex.IsMatch(ipAddress)));
        }

        /// <summary>
        /// Check if an ip address, dns name or user name is blacklisted
        /// </summary>
        /// <param name="ipAddressDnsOrUserName">IP address, dns name or user name</param>
        /// <returns>True if blacklisted, false otherwise</returns>
        public bool IsBlackListed(string ipAddressDnsOrUserName)
        {
            return !string.IsNullOrWhiteSpace(ipAddressDnsOrUserName) &&
                ((blackList.Contains(ipAddressDnsOrUserName) ||
                (blackListRegex != null && blackListRegex.IsMatch(ipAddressDnsOrUserName))));
        }

        /// <summary>
        /// Check if a user name is whitelisted
        /// </summary>
        /// <param name="userName">User name</param>
        /// <returns>True if whitelisted, false otherwise</returns>
        public bool IsUserNameWhitelisted(string userName)
        {
            if (string.IsNullOrEmpty(userName))
            {
                return false;
            }
            userName = userName.ToUpperInvariant().Normalize();
            return userNameWhitelist.Contains(userName);
        }

        /// <summary>
        /// Checks if a user name is within the maximum edit distance for the user name whitelist.
        /// If userName is null or empty this method returns true.
        /// If the user name whitelist is empty, this method returns true.
        /// </summary>
        /// <param name="userName">User name</param>
        /// <returns>True if within max edit distance of any whitelisted user name, false otherwise.</returns>
        public bool IsUserNameWithinMaximumEditDistanceOfUserNameWhitelist(string userName)
        {
            if (userNameWhitelist.Count == 0 || string.IsNullOrEmpty(userName))
            {
                return true;
            }

            userName = userName.ToUpperInvariant().Normalize();
            foreach (string userNameInWhitelist in userNameWhitelist)
            {
                int distance = LevenshteinUnsafe.Distance(userName, userNameInWhitelist);
                if (distance <= userNameWhitelistMaximumEditDistance)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Return all the groups that match the specified keywords (Windows only)
        /// </summary>
        /// <param name="keywords">Keywords</param>
        /// <returns>Groups that match</returns>
        public IEnumerable<ExpressionsToBlockGroup> WindowsEventViewerGetGroupsMatchingKeywords(ulong keywords)
        {
            return WindowsEventViewerExpressionsToBlock.Groups.Where(g => (g.KeywordsULONG == keywords));
        }

        /// <summary>
        /// Number of failed login attempts before a ban is initiated
        /// </summary>
        public int FailedLoginAttemptsBeforeBan { get { return failedLoginAttemptsBeforeBan; } }

        /// <summary>
        /// Length of time to ban an ip address
        /// </summary>
        public TimeSpan BanTime { get { return banTime; } }

        /// <summary>
        /// The duration after the last failed login attempt that the count is reset back to 0.
        /// </summary>
        public TimeSpan ExpireTime { get { return expireTime; } }
        
        /// <summary>
        /// Interval of time to do house-keeping chores like un-banning ip addresses
        /// </summary>
        public TimeSpan CycleTime { get { return cycleTime; } }

        /// <summary>
        /// The minimum time between failed login attempts to increment the ban counter
        /// </summary>
        public TimeSpan MinimumTimeBetweenFailedLoginAttempts { get { return minimumTimeBetweenFailedLoginAttempts; } }

        /// <summary>
        /// Rule prefix for firewall
        /// </summary>
        public string FirewallRulePrefix { get { return firewallRulePrefix; } }

        /// <summary>
        /// Event viewer expressions to block (Windows only)
        /// </summary>
        public ExpressionsToBlock WindowsEventViewerExpressionsToBlock { get { return expressions; } }

        /// <summary>
        /// Log files to parse
        /// </summary>
        public IReadOnlyCollection<LogFileToParse> LogFilesToParse { get { return logFiles; } }

        /// <summary>
        /// True to clear and unban ip addresses upon restart, false otherwise
        /// </summary>
        public bool ClearBannedIPAddressesOnRestart { get { return clearBannedIPAddressesOnRestart; } }

        /// <summary>
        /// Black list of ips as a comma separated string
        /// </summary>
        public string BlackList { get { return string.Join(",", blackList); } }

        /// <summary>
        /// Black list regex
        /// </summary>
        public string BlackListRegex { get { return (blackListRegex == null ? string.Empty : blackListRegex.ToString()); } }

        /// <summary>
        /// White list of ips as a comma separated string
        /// </summary>
        public string WhiteList { get { return string.Join(",", whiteList); } }

        /// <summary>
        /// Whether to create a firewall rule to allow all whitelisted ip addresses access to all ports
        /// </summary>
        public bool CreateWhitelistFirewallRule { get { return createWhitelistFirewallRule; } }

        /// <summary>
        /// White list regex
        /// </summary>
        public string WhiteListRegex { get { return (whiteListRegex == null ? string.Empty : whiteListRegex.ToString()); } }

        /// <summary>
        /// White list user names. Any user name found not in the list is banned.
        /// </summary>
        public IReadOnlyCollection<string> UserNameWhitelist { get { return userNameWhitelist; } }

        /// <summary>
        /// Number of failed logins before banning a user name in the user name whitelist
        /// </summary>
        public int FailedLoginAttemptsBeforeBanUserNameWhitelist { get { return failedLoginAttemptsBeforeBanUserNameWhitelist; } }

        /// <summary>
        /// Dictionary of string operating system name (Windows, Linux, OSX, etc.) and firewall class type
        /// </summary>
        public IReadOnlyDictionary<string, string> FirewallOSAndType { get { return osAndFirewallType; } }

        /// <summary>
        /// Process to run on ban. See ReplaceUrl of IPBanService for place-holders.
        /// </summary>
        public string ProcessToRunOnBan { get { return processToRunOnBan; } }

        /// <summary>
        /// A url to get when the service updates, empty for none. See ReplaceUrl of IPBanService for place-holders.
        /// </summary>
        public string GetUrlUpdate { get { return getUrlUpdate; } }

        /// <summary>
        /// A url to get when the service starts, empty for none. See ReplaceUrl of IPBanService for place-holders.
        /// </summary>
        public string GetUrlStart { get { return getUrlStart; } }

        /// <summary>
        /// A url to get when the service stops, empty for none. See ReplaceUrl of IPBanService for place-holders.
        /// </summary>
        public string GetUrlStop { get { return getUrlStop; } }

        /// <summary>
        /// A url to get for a config file update, empty for none. See ReplaceUrl of IPBanService for place-holders.
        /// </summary>
        public string GetUrlConfig { get { return getUrlConfig; } }

        /// <summary>
        /// Url to query to get the external ip address, the url should return a string which is the external ip address.
        /// </summary>
        public string ExternalIPAddressUrl { get { return externalIPAddressUrl; } }
    }
}
