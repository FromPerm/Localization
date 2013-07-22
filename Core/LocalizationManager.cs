﻿using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Configuration;
using System;
using System.Web;
using System.Threading;

namespace Knoema.Localization
{
	public sealed class LocalizationManager
	{
		public const string CookieName = "current-lang";
		public const string QueryParameter = "lang";

		private static object _lock = new object();
		public static ILocalizationRepository Repository { get; set; }

		private static readonly LocalizationManager _instanse = new LocalizationManager();
		public static LocalizationManager Instance
		{
			get
			{
				return _instanse;
			}
		}

		private LocalizationManager() { }

		public string Translate(string scope, string text)
		{
			if (string.IsNullOrEmpty(scope))
				throw new ArgumentNullException("Scope cannot be null.");

			if (string.IsNullOrEmpty(text))
				throw new ArgumentNullException("Text cannot be null.");

			var cultures = GetCultures().ToList();

			if (cultures.Count > 0 && !cultures.Contains(CultureInfo.CurrentCulture))
				return null;

			var hash = GetHash(scope.ToLowerInvariant() + text);

			// get object from cache...
			var obj = GetLocalizedObject(CultureInfo.CurrentCulture, hash);

			// if null save object to db for all cultures 
			if (obj == null)
			{
				if (!cultures.Contains(DefaultCulture.Value))
					cultures.Add(DefaultCulture.Value);

				foreach (var culture in cultures)
				{
					lock (_lock)
					{
						var stored = GetLocalizedObject(culture, hash);
						if (stored == null)
							Save(Create(hash, culture.LCID, scope, text));
					}
				}
			}
			else
				return obj.Translation;

			return null;
		}

		public void CreateCulture(CultureInfo culture)
		{
			var res = new List<ILocalizedObject>();

			var lst = GetAll(DefaultCulture.Value);
			foreach (var obj in lst)
			{
				var stored = GetLocalizedObject(culture, obj.Hash);
				if (stored == null)
					res.Add(Create(obj.Hash, culture.LCID, obj.Scope, obj.Text));
			}

			Save(res.ToArray());
		}

		public ILocalizedObject Create(string hash, int localeId, string scope, string text)
		{
			var result = Repository.Create();
			result.Hash = hash;
			result.LocaleId = localeId;
			result.Scope = scope;
			result.Text = text;

			return result;
		}

		public ILocalizedObject Get(int key)
		{
			if(!LocalizationCache.Available)
				return Repository.Get(key);

			var obj = LocalizationCache.Get<ILocalizedObject>(key.ToString());
			if (obj == null)
			{
				obj = Repository.Get(key);
				LocalizationCache.Insert(key.ToString(), obj);
			}

			return obj;
		}

		public IEnumerable<ILocalizedObject> GetScriptResources(CultureInfo culture)
		{
			if (!GetCultures().Contains(culture))
				return Enumerable.Empty<ILocalizedObject>();

			return GetAll(culture).Where(x => (x.Scope != null) && (x.Scope.EndsWith("js") || x.Scope.EndsWith("htm")));
		}

		public IEnumerable<ILocalizedObject> GetAll(CultureInfo culture)
		{
			if(!LocalizationCache.Available)
				return Repository.GetAll(culture).ToList();

			var lst = LocalizationCache.Get<IEnumerable<ILocalizedObject>>(culture.Name);
			if (lst == null || lst.Count() == 0)
			{
				lst = Repository.GetAll(culture).ToList();
				LocalizationCache.Insert(culture.Name, lst);
			}

			return lst;
		}

		public IEnumerable<CultureInfo> GetCultures()
		{
			if(!LocalizationCache.Available)
				return Repository.GetCultures().ToList();

			var lst = LocalizationCache.Get<IEnumerable<CultureInfo>>("cultures");
			if (lst == null || lst.Count() == 0)
			{
				lst = Repository.GetCultures().ToList();
				LocalizationCache.Insert("cultures", lst);
			}

			return lst;
		}


		public void Delete(params ILocalizedObject[] list)
		{
			Repository.Delete(list);

			// clear cache 
			if (LocalizationCache.Available)
				LocalizationCache.Clear();
		}

		public void Save(params ILocalizedObject[] list)
		{
			Repository.Save(list);

			// clear cache 
			if (LocalizationCache.Available)
				LocalizationCache.Clear();
		}

		public void Import(params ILocalizedObject[] list)
		{
			var import = new List<ILocalizedObject>();
			foreach (var obj in list)
			{
				var stored = GetLocalizedObject(new CultureInfo(obj.LocaleId), obj.Hash);
				if (stored != null)
				{
					if (!string.IsNullOrEmpty(obj.Translation))
					{
						stored.Translation = obj.Translation;
						import.Add(stored);
					}
				}
				else
				{
					var imported = Create(obj.Hash, obj.LocaleId, obj.Scope, obj.Text);
					imported.Translation = obj.Translation;

					import.Add(imported);
				}

				// check object for default culture
				var def = GetLocalizedObject(DefaultCulture.Value, obj.Hash);
				if (def == null)
					import.Add(Create(obj.Hash, DefaultCulture.Value.LCID, obj.Scope, obj.Text));
			}

			Save(import.ToArray());
		}

		public string FormatScope(Type type)
		{
			var scope = type.Assembly.FullName.Split(',').Length > 0
					? type.FullName.Replace(type.Assembly.FullName.Split(',')[0], "~")
					: type.FullName;

			return scope.Replace(".", "/");
		}

		public IEnumerable<ILocalizedObject> GetLocalizedObjects(CultureInfo culture, string text, bool strict = true)
		{
			if (strict)
				return GetAll(culture).Where(x => x.Text.ToLowerInvariant() == text.ToLowerInvariant());
			else
				return GetAll(culture).Where(x => x.Text.ToLowerInvariant().Contains(text.ToLowerInvariant()));
		}

		public void SetCulture(CultureInfo culture)
		{			
			Thread.CurrentThread.CurrentCulture = Thread.CurrentThread.CurrentUICulture = culture;
			HttpContext.Current.Response.Cookies.Add(new HttpCookie(CookieName, culture.Name)
			{
				Expires = DateTime.Now.AddYears(1),
			});
		}

		public string GetCulture()
		{
			return Thread.CurrentThread.CurrentCulture.Name;
		}

		public IList<string> GetBrowserCultures()
		{
			var cultures = new List<string>();
			
			if (HttpContext.Current == null)
				return cultures;		

			var browser = HttpContext.Current.Request.UserLanguages;
			if (browser != null)			
				foreach(var culture in browser)
				{
					var lang = culture.IndexOf(';') > -1 
						? culture.Split(';')[0] 
						: culture;

					cultures.Add(lang);
				}		
						
			return cultures.Distinct().ToList();
		}

		public string GetCultureFromCookie()
		{
			var cookie = HttpContext.Current.Request.Cookies[LocalizationManager.CookieName];
			
			if (cookie != null)
				return cookie.Value;

			return null;
		}

		public string GetCultureFromQuery()
		{
			return HttpContext.Current.Request.QueryString[LocalizationManager.QueryParameter];		
		}

		public void InsertScope(string path)
		{
			if (HttpContext.Current == null)
				return;

			var scope = HttpContext.Current.Items["localizationScope"] as List<string> ?? new List<string>();

			if (!scope.Contains(path))
				scope.Add(path);

			HttpContext.Current.Items["localizationScope"] = scope;
		}

		public List<string> GetScope()
		{
			if (HttpContext.Current == null)
				return null;

			return HttpContext.Current.Items["localizationScope"] as List<string>;
		}

		private ILocalizedObject GetLocalizedObject(CultureInfo culture, string hash)
		{
			return GetAll(culture).FirstOrDefault(x => x.Hash == hash);
		}

		private string GetHash(string text)
		{
			var hash = new MD5CryptoServiceProvider().ComputeHash(Encoding.UTF8.GetBytes(text));
			var stringBuilder = new StringBuilder();

			for (var i = 0; i < hash.Length; i++)
				stringBuilder.Append(hash[i].ToString("x2"));

			return stringBuilder.ToString();
		}
	}
}
