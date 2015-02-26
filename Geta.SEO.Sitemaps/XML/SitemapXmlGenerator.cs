﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web;
using System.Web.Caching;
using System.Xml;
using System.Xml.Linq;
using EPiServer;
using EPiServer.Core;
using EPiServer.Logging.Compatibility;
using EPiServer.Web;
using EPiServer.Web.Routing;
using Geta.SEO.Sitemaps.Entities;
using Geta.SEO.Sitemaps.Repositories;
using Geta.SEO.Sitemaps.Utils;
using System.Reflection;

namespace Geta.SEO.Sitemaps.XML
{
    public abstract class SitemapXmlGenerator
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(SitemapXmlGenerator));

        protected readonly ISitemapRepository SitemapRepository;
        protected readonly IContentRepository ContentRepository;
        protected readonly UrlResolver UrlResolver;
        protected readonly SiteDefinitionRepository SiteDefinitionRepository;

        private const int MaxSitemapEntryCount = 50000;

        private SitemapData _sitemapData;
        private readonly ISet<string> _urlSet;
        private SiteDefinition _settings;
        private string _hostLanguageBranch;

        protected SitemapXmlGenerator(ISitemapRepository sitemapRepository, IContentRepository contentRepository, UrlResolver urlResolver, SiteDefinitionRepository siteDefinitionRepository)
        {
            this.SitemapRepository = sitemapRepository;
            this.ContentRepository = contentRepository;
            this.UrlResolver = urlResolver;
            this.SiteDefinitionRepository = siteDefinitionRepository;
            this._urlSet = new HashSet<string>();
        }

        protected abstract XElement GenerateSiteElement(IContent contentData, string url, DateTime? overrideSavedDate = null); //changed for project compatibility

        protected abstract XElement GenerateRootElement();

        /// <summary>
        /// Generates a xml sitemap about pages on site
        /// </summary>
        /// <param name="sitemapData">SitemapData object containing configuration info for sitemap</param>
        /// <param name="entryCount">out count of site entries in generated sitemap</param>
        /// <returns>True if sitemap generation successful, false if error encountered</returns>
        public virtual bool Generate(SitemapData sitemapData, out int entryCount)
        {
            try
            {
                this._sitemapData = sitemapData;
                var sitemapSiteUri = new Uri(this._sitemapData.SiteUrl);
                this._settings = GetSiteDefinitionFromSiteUri(sitemapSiteUri);
                this._hostLanguageBranch = GetHostLanguageBranch();
                XElement sitemap = CreateSitemapXmlContents(out entryCount);

                var doc = new XDocument(new XDeclaration("1.0", "utf-8", null));
                doc.Add(sitemap);

                using (var ms = new MemoryStream())
                {
                    var xtw = new XmlTextWriter(ms, Encoding.UTF8);
                    doc.Save(xtw);
                    xtw.Flush();
                    sitemapData.Data = ms.ToArray();
                }

                this.SitemapRepository.Save(sitemapData);

                return true;
            }
            catch (Exception e)
            {
                Log.Error("Error on generating xml sitemap" + Environment.NewLine + e);
                entryCount = 0;
                return false;
            }
        }

        /// <summary>
        /// Creates xml content for a given sitemap configuration entity
        /// </summary>
        /// <param name="entryCount">out: count of sitemap entries in the returned element</param>
        /// <returns>XElement that contains sitemap entries according to the configuration</returns>
        private XElement CreateSitemapXmlContents(out int entryCount)
        {
            XElement sitemapElement = GenerateRootElement();

            sitemapElement.Add(GetSitemapXmlElements());

            entryCount = _urlSet.Count;
            return sitemapElement;
        }

        protected virtual IEnumerable<XElement> GetSitemapXmlElements()
        {

            if (this._settings == null)
            {
                return Enumerable.Empty<XElement>();
            }

            var rootPage = this._sitemapData.RootPageId < 0 ? this._settings.StartPage : new ContentReference(this._sitemapData.RootPageId);

            IList<ContentReference> descendants = this.ContentRepository.GetDescendents(rootPage).ToList();

            if (rootPage != ContentReference.RootPage)
            {
                descendants.Add(rootPage);
            }

            return GenerateXmlElements(descendants);
        }

        protected virtual IEnumerable<XElement> GenerateXmlElements(IEnumerable<ContentReference> pages)
        {
            IList<XElement> sitemapXmlElements = new List<XElement>();

            foreach (ContentReference contentReference in pages)
            {
                var languagePages = this.ContentRepository.GetLanguageBranches<IContentData>(contentReference).OfType<IContent>();

                foreach (var page in languagePages)
                {
                    if ((page is PageData) && ExcludePageLanguageFromSitemap((PageData)page))
                    {
                        continue;
                    }

                    if (this._urlSet.Count >= MaxSitemapEntryCount)
                    {
                        this._sitemapData.ExceedsMaximumEntryCount = true;
                        return sitemapXmlElements;
                    }

                    AddFilteredPageElement(page, sitemapXmlElements);

                    //changed for project compatibility
                    //some pages render data from external systems based on the virtual path following the actual page url
                    //these changes allow us to generate sitemap entries for these "virtual" pages
                    var fullPageUrl = this._urlSet.Last();
                    var pageType = page.GetType();
                    if (pageType.GetInterface("Komm.Models.Pages.IHasDefaultActionWithSitemap") == null) continue;

                    var mi = pageType.GetMethod("GetAllVirtualChildren", BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                    if (mi == null) continue;

                    var ret = mi.Invoke(page, null);
                    if (ret == null) continue;

                    var items = ret as Dictionary<string, DateTime?>;
                    foreach (var item in items)
                    {
                        if (this._urlSet.Count >= MaxSitemapEntryCount)
                        {
                            this._sitemapData.ExceedsMaximumEntryCount = true;
                            return sitemapXmlElements;
                        }

                        var virtualFullPageUrl = fullPageUrl.ToString() + item.Key + "/";
                        XElement pageElement = this.GenerateSiteElement(page, virtualFullPageUrl, item.Value);

                        sitemapXmlElements.Add(pageElement);
                        this._urlSet.Add(virtualFullPageUrl);
                    }
                    //end of changes
                }
            }

            return sitemapXmlElements;
        }

        private SiteDefinition GetSiteDefinitionFromSiteUri(Uri sitemapSiteUri)
        {
            return this.SiteDefinitionRepository
                .List()
                .FirstOrDefault(siteDef => siteDef.SiteUrl == sitemapSiteUri || siteDef.Hosts.Any(hostDef => hostDef.Name.Equals(sitemapSiteUri.Host, StringComparison.InvariantCultureIgnoreCase)));
        }

        private string GetHostLanguageBranch()
        {
            var hostDefinition = GetHostDefinition();

            return hostDefinition != null && hostDefinition.Language != null
                ? hostDefinition.Language.ToString()
                : null;
        }

        private bool HostDefinitionExistsForLanguage(string languageBranch)
        {
            var cacheKey = string.Format("HostDefinitionExistsFor{0}-{1}", this._sitemapData.SiteUrl, languageBranch);
            object cachedObject = HttpRuntime.Cache.Get(cacheKey);

            if (cachedObject == null)
            {
                cachedObject =
                    this._settings.Hosts.Any(
                        x =>
                        x.Language != null &&
                        x.Language.ToString().Equals(languageBranch, StringComparison.InvariantCultureIgnoreCase));

                HttpRuntime.Cache.Insert(cacheKey, cachedObject, null, DateTime.Now.AddMinutes(10), Cache.NoSlidingExpiration);
            }

            return (bool)cachedObject;
        }

        private HostDefinition GetHostDefinition()
        {
            var siteUrl = new Uri(this._sitemapData.SiteUrl);
            string sitemapHost = siteUrl.Host;

            return this._settings.Hosts.FirstOrDefault(x => x.Name.Equals(sitemapHost, StringComparison.InvariantCultureIgnoreCase)) ??
                   this._settings.Hosts.FirstOrDefault(x => x.Name.Equals(SiteDefinition.WildcardHostName));
        }

        /// <summary>
        /// Check if the page languagebranch should be excluded from the current sitemap.
        /// </summary>
        /// <param name="page">PageData</param>
        /// <returns>True if the current sitemap host is mapped to a specific language and the page languagebranch doesn't match this language AND if a HostDefinition mapped to the page.LanguageBranch exists, otherwise false.</returns>
        private bool ExcludePageLanguageFromSitemap(PageData page)
        {
            return this._hostLanguageBranch != null &&
               !this._hostLanguageBranch.Equals(page.LanguageBranch, StringComparison.InvariantCultureIgnoreCase) &&
               HostDefinitionExistsForLanguage(page.LanguageBranch);

        }

        private void AddFilteredPageElement(IContent contentData, IList<XElement> xmlElements)
        {
            if (contentData is PageData && PageFilter.ShouldExcludePage((PageData)contentData))
            {
                return;
            }

            string url;

            if (contentData is PageData)
            {
                var tempPage = (PageData)contentData;
                url = this.UrlResolver.GetUrl(contentData.ContentLink, tempPage.LanguageBranch);

                // Make 100% sure we remove the language part in the URL if the sitemap host is mapped to the page's LanguageBranch.
                if (this._hostLanguageBranch != null && tempPage.LanguageBranch.Equals(this._hostLanguageBranch, StringComparison.InvariantCultureIgnoreCase))
                {
                    url = url.Replace(string.Format("/{0}/", this._hostLanguageBranch), "/");
                }
            }
            else
            {
                url = this.UrlResolver.GetUrl(contentData.ContentLink);
            }

            Uri absoluteUri;

            // if the URL is relative we add the base site URL (protocol and hostname)
            if (!IsAbsoluteUrl(url, out absoluteUri))
            {
                url = UriSupport.Combine(this._sitemapData.SiteUrl, url);
            }
            // Force the SiteUrl
            else
            {
                url = UriSupport.Combine(this._sitemapData.SiteUrl, absoluteUri.AbsolutePath);
            }

            var fullPageUrl = new Uri(url);

            if (this._urlSet.Contains(fullPageUrl.ToString()) || UrlFilter.IsUrlFiltered(fullPageUrl.AbsolutePath, this._sitemapData))
            {
                return;
            }

            XElement pageElement = this.GenerateSiteElement(contentData, fullPageUrl.ToString());

            xmlElements.Add(pageElement);
            this._urlSet.Add(fullPageUrl.ToString());
        }

        private bool IsAbsoluteUrl(string url, out Uri absoluteUri)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out absoluteUri);
        }
    }
}