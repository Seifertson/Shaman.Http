﻿using Fizzler.Systems.HtmlAgilityPack;
#if !SALTARELLE
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
#endif
using Shaman.Dom;
using Shaman.Runtime;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
#if SALTARELLE
using System.Text.Saltarelle;
#else
using System.Text;
#if NATIVE_HTTP
using System.Net;
using System.Net.Http;
#else
using System.Net.Reimpl;
using System.Net.Reimpl.Http;
#endif
using HttpStatusCode = System.Net.HttpStatusCode;
#endif
using System.Threading.Tasks;
#if !STANDALONE
using HttpUtils = Shaman.Utils;
using HttpExtensionMethods = Shaman.ExtensionMethods;
#endif
#if NET35
using HttpResponseMessage = System.Net.HttpWebResponse;
using HttpRequestMessage = System.Net.HttpWebRequest;
#else
using TaskEx = System.Threading.Tasks.Task;
#endif



namespace Shaman
{
#if STANDALONE
    public static partial class HttpExtensionMethods
#else
    public static partial class ExtensionMethods
#endif
    {
#if !SALTARELLE
        public static void AppendUriEncoded(this StringBuilder sb, string text)
        {
            sb.Append(HttpUtils.EscapeDataString(text));
        }

        public static void AppendHtmlEncoded(this StringBuilder sb, string text, int startIndex = 0, int endIndex = -1)
        {
            using (var s = new StringWriter(sb))
            {
                s.WriteHtmlEncoded(text, startIndex: startIndex, endIndex: endIndex);
            }
        }





#if STANDALONE
        static HttpExtensionMethods()
        {
            FizzlerCustomSelectors.RegisterAll();
        }
#endif
        internal static void AbortAndDispose(this HttpResponseMessage response)
        {
            response.Dispose();
        }


        internal class GetHtmlOrJsonAsyncResponse
        {
            public HtmlNode Node;
            public Uri RedirectUrl;
            public WebCache CacheData;
            public Exception Exception;
        }

        internal static async Task<GetHtmlOrJsonAsyncResponse> GetHtmlOrJsonAsync(LazyUri url, WebRequestOptions options,
#if NET35
        IDictionary<string, string> metaParameters,
#else
        IReadOnlyDictionary<string, string> metaParameters,
#endif 
        Credentials credentialsForVary, bool needsCache)
        {

            var messageBox = new HttpRequestMessageBox();
            var noredir = metaParameters.TryGetValue("$noredir") == "1";
            HttpResponseInfo info = null;
            long contentLength = -1;
            try
            {


                var cacheData = needsCache ? new WebCache() : null;


                Uri redirectLocation = null;

                var sw = Stopwatch.StartNew();
                try
                {
                    await Utils.CreateTask(async () =>
                    {

                        info = await SendAsync(url, options, messageBox, alwaysCatchAndForbidRedirects: true, keepResponseAliveOnError: true);
#if NET35
                        contentLength = info.Response != null ? info.Response.ContentLength : -1;
#else
                        contentLength = (info.Response?.Content?.Headers.ContentLength).GetValueOrDefault(-1);
#endif

                        if (info.Exception != null)
                        {
                            var webex = info.Exception as WebException;
                            if (webex == null || webex.Status != HttpUtils.UnexpectedRedirect)
                            {
                                using (info.Response)
                                {
                                    var errorSelector = metaParameters.TryGetValue("$error-status-selector") ?? metaParameters.TryGetValue("$error-selector");
                                    if (errorSelector != null && info.Response != null)
                                    {
                                        var parsed = await ParseHtmlAsync(info, null, null, options, metaParameters, url);
                                        var err = parsed.TryGetValue(errorSelector);
                                        if (err != null)
                                        {
                                            throw new WebException("The page reports: " + err, info.Exception);
                                        }
                                    }
                                    throw info.Exception;
                                }

                            }

                            if (info.Response != null)
                                info.Response.Dispose();

#if NET35
                            var vv = info.Response.Headers["Location"];
                            redirectLocation = vv != null ? HttpUtils.GetAbsoluteUri(url.PathConsistentUrl, vv) : null;
#else
                            redirectLocation = info.Response.Headers.Location;
#endif
                            
                            if (redirectLocation.IsAbsoluteUri && redirectLocation.Scheme == HttpUtils.UriSchemeFile)
                            {
                                if (!redirectLocation.OriginalString.StartsWith("/")) throw new ArgumentException("Redirect URL must either be absolute, or start with '/'.");
                                redirectLocation = new Uri(url.Scheme + "://" + url.Authority + redirectLocation.OriginalString);
                            }
                            else if (!redirectLocation.IsAbsoluteUri)
                            {
                                redirectLocation = new Uri((url.Scheme + "://" + url.Authority).AsUri(), redirectLocation);
                            }


                            if (cacheData != null)
                            {
                                cacheData.RedirectUrl = redirectLocation;
                                cacheData.ErrorCode = (int)info.Response.StatusCode;
                            }
                            if (!noredir && redirectLocation == null) throw new Exception("Redirect without Location header was received.");
                        }
                    }).WithTimeout(TimeSpan.FromMilliseconds(options.Timeout));
                }
                catch (AggregateException ex) when (ex.InnerException != null)
                {
                    throw ex.InnerException;
                }


                using (var response = info.Response)
                {
                    if (cacheData != null) cacheData.ErrorCode = (int)response.StatusCode;
                    IEnumerable<string> cookies;
#if NET35
                    cookies = response.Headers.GetValues("Set-Cookie");
#else
                    if (response.Headers.TryGetValues("Set-Cookie", out cookies))
#endif
                    if (cookies != null)
                    {
                        foreach (var cookie in cookies)
                        {
                            var p = cookie.IndexOf(';');
                            var keyval = p != -1 ? cookie.Substring(0, p) : cookie;
                            var eq = keyval.IndexOf('=');
                            if (eq != -1)
                            {
                                var key = keyval.Substring(0, eq).Trim();
                                var val = keyval.Substring(eq + 1).Trim();
                                options.AddCookie(key, val, PriorityCookie.PRIORITY_SetCookie);
                                if (cacheData != null) cacheData.Cookies[key] = val;

                            }
                        }
                    }
                    var html = await ParseHtmlAsync(info, noredir ? null : redirectLocation, cacheData, options, metaParameters, url);
                    return new GetHtmlOrJsonAsyncResponse()
                    {
                        CacheData = cacheData,
                        RedirectUrl = redirectLocation,
                        Node = html
                    };
                }
            }
            catch (Exception ex)
            {
                return new GetHtmlOrJsonAsyncResponse()
                {
                    CacheData = needsCache ? Caching.GetWebCacheForException(ex, info?.RespondingUrl, contentLength) : null,
                    Exception = ex
                };
            }
            finally
            {
                messageBox.Dispose();

                if (info != null && info.Response != null)
                {
                    info.Response.Dispose();
                    info.Response = null;
                }
                info = null;
            }



        }


        public static string GetCookieValue(this
#if NATIVE_HTTP
            System.Net.CookieCollection cookies,
#else
            System.Net.Reimpl.CookieCollection cookies,
#endif
            string name)
        {
            var c = cookies[name];
            if (c == null) return null;
            return !string.IsNullOrEmpty(c.Value) ? c.Value : null;
        }

             internal static void AppendUriEncoded(this NakedStringBuilder sb, string text)
        {
#if DESKTOP
            sb.Data = DirectUriEscapeChar.EscapeString(text, 0, text.Length, sb.Data, ref sb.Length, false, DirectUriEscapeChar.c_DummyChar, DirectUriEscapeChar.c_DummyChar, DirectUriEscapeChar.c_DummyChar);
#else
            var s = Uri.EscapeDataString(text);
            sb.Append(text);
#endif
        }
     



        private static async Task<HtmlNode> ParseHtmlAsync(HttpResponseInfo info, Uri redirectLocation, WebCache cacheData, WebRequestOptions options,
#if NET35
        IDictionary<string, string> metaParameters,
#else
        IReadOnlyDictionary<string, string> metaParameters,
#endif
        LazyUri url)
        {


            HtmlNode html = null;


            var response = info.Response;
            if (redirectLocation == null)
            {
                Encoding initialEncoding = Encoding.UTF8;

                if (options.ResponseEncoding != null)
                {
                    initialEncoding = options.ResponseEncoding;
                }
                else
                {
                    var encoding = metaParameters.TryGetValue("$response-encoding");
                    if (encoding != null)
                    {
                        initialEncoding = Encoding.GetEncoding(encoding);
                    }
                    else
                    {
#if WEBCLIENT
                        var charset = HttpUtils.GetCharSetFromContentType(response.Headers["Content-Type"]);
#else
                        var charset = response.Content.Headers.ContentType?.CharSet;
#endif
                        if (charset != null)
                        {
                            try
                            {
                                if (charset == "utf-8" || charset.Equals("\"utf-8\"", StringComparison.OrdinalIgnoreCase)) initialEncoding = Encoding.UTF8;
                                else initialEncoding = Encoding.GetEncoding(charset);
                            }
                            catch (Exception)
                            {
                            }
                        }
                    }
                }

#if NET35
                var content = response;
#else
                var content = response.Content;
#endif


                var contentType = (metaParameters != null ? metaParameters.TryGetValue("$content-type") : null) ??
#if WEBCLIENT
                    HttpUtils.GetMimeFromContentType(content.Headers["Content-Type"]);
#else 
                    (content.Headers.ContentType != null ? content.Headers.ContentType.MediaType : null);
#endif
                var jsonToken = metaParameters != null ? metaParameters.TryGetValue("$json-token") : null;
                var jsonWrappedHtmlSelector = metaParameters != null ? metaParameters.TryGetValue("$json-wrapped-html") : null;
                var htmlWrappedJsonSelector = metaParameters != null ? metaParameters.TryGetValue("$html-wrapped-json") : null;
                var jsonContentType = (htmlWrappedJsonSelector == null && (jsonToken != null || jsonWrappedHtmlSelector != null) ||
                    (contentType != null && (contentType.Contains("json", StringComparison.OrdinalIgnoreCase) || contentType.Contains("script", StringComparison.OrdinalIgnoreCase))));
                var looksLikeHtml = metaParameters != null && metaParameters.TryGetValue("$assume-text") == "1";
                var plainText = metaParameters != null && metaParameters.TryGetValue("$assume-text") == "1";
                if (jsonWrappedHtmlSelector != null && htmlWrappedJsonSelector != null) throw new ArgumentException("Cannot have both $json-wrapped-html and $html-wrapped-json metaparameters. Use extraction attributes on individual fields for more complex selections.");
                //var remaining = TimeSpan.FromMilliseconds(Math.Max(0, options.Timeout - sw.ElapsedMilliseconds));
                if (cacheData != null)
                    cacheData.Url = url;

                if (contentType != null && contentType.StartsWith("image/")) throw new NotSupportedResponseException(contentType, url);

                await TaskEx.Run(
#if !NET35
                    async
#endif
                     () =>
                {
                    LazyTextReader lazy = null;
                    try
                    {
#if WEBCLIENT
                        var stream = content.GetResponseStream();
#else
                        await content.LoadIntoBufferAsync().ConfigureAwait(false);

                        var stream = await content.ReadAsStreamAsync().ConfigureAwait(false);
#endif

                        lazy = new LazyTextReader(stream, initialEncoding);



                        if (!looksLikeHtml && !plainText)
                        {
                            looksLikeHtml = HttpUtils.LooksLikeHtml(lazy);
                        }

                        string allText = null;

                        if (!looksLikeHtml && !plainText && htmlWrappedJsonSelector == null)
                        {
                            try
                            {
                                string json;
                                if (allText == null)
                                {
                                    lazy.ReadToEnd();
                                    allText = lazy.ToString();
                                }
                                if (jsonToken != null)
                                {
                                    json = allText;
                                    var start = json.IndexOf(jsonToken);
                                    if (start == -1) throw new ExtractionException(sourceData: json, beginString: jsonToken);
                                    start += jsonToken.Length;
                                    json = json.Substring(start);
                                }
                                else
                                {
                                    json = CleanupJsonp(allText);
                                }




                                html = FizzlerCustomSelectors.JsonToHtml(json, 0, null);
                                html.OwnerDocument.SetPageUrl(url);



                                if (jsonWrappedHtmlSelector != null)
                                {
                                    var doc = FizzlerCustomSelectors.CreateDocument(html.OwnerDocument);
                                    var t = html.FindAll(jsonWrappedHtmlSelector);
                                    var gt = t.GetText() ?? string.Empty;
                                    html = FizzlerCustomSelectors.ReparseHtml(doc, gt, html.OwnerDocument);

                                    if (cacheData != null)
                                    {
                                        cacheData.DataType = WebCacheDataType.Html;
                                        cacheData.Result = gt;
                                    }

                                }
                                else
                                {
                                    if (cacheData != null)
                                    {
                                        cacheData.DataType = WebCacheDataType.Json;
                                        cacheData.Result = json;
                                    }
                                }




                            }
                            catch when (!jsonContentType)
                            {
                                if (!looksLikeHtml) throw new NotSupportedResponseException(contentType, url);
                            }
                        }

                        if (cacheData != null)
                            cacheData.Url = info.RespondingUrl ?? url;

                        if (html == null)
                        {
                            var d = new HtmlDocument();
                            if (plainText)
                            {
                                if (allText == null)
                                {
                                    lazy.ReadToEnd();
                                    allText = lazy.ToString();
                                }


                                if (cacheData != null)
                                {
                                    cacheData.Result = allText;
                                    cacheData.DataType = WebCacheDataType.Text;
                                }
                                html = d.DocumentNode;
                                html.AppendTextNode(allText);
                                html.SetAttributeValue("plain-text", "1");
                            }
                            else
                            {
                                if (allText != null) d.LoadHtml(allText);
                                else d.Load(lazy);
                                lazy.ReadToEnd();

                                if (cacheData != null)
                                {
                                    cacheData.Result = allText ?? lazy.ToString();
                                    cacheData.DataType = WebCacheDataType.Html;
                                }
                                html = d.DocumentNode;


                                if (htmlWrappedJsonSelector != null)
                                {
                                    var script = d.DocumentNode.FindSingle(htmlWrappedJsonSelector);
                                    if (script == null) throw new ExtractionException(node: d.DocumentNode, nodeQuery: htmlWrappedJsonSelector, message: "No matching nodes for $html-wrapped-json metaparameter.");
                                    var text = script.InnerText;
                                    string json;
                                    if (jsonToken != null)
                                    {
                                        json = text;
                                        var start = json.IndexOf(jsonToken);
                                        if (start == -1) throw new ExtractionException(sourceData: json, beginString: jsonToken);
                                        start += jsonToken.Length;
                                        json = json.Substring(start);
                                    }
                                    else
                                    {
                                        json = CleanupJsonp(allText);
                                    }

                                    if (cacheData != null)
                                    {
                                        cacheData.Result = json;
                                        cacheData.DataType = WebCacheDataType.Json;
                                    }

                                    html = FizzlerCustomSelectors.JsonToHtml(json, 0, null);
                                    html.OwnerDocument.SetPageUrl(url);


                                }
                            }

                        }






                        var docnode = html.OwnerDocument.DocumentNode;
                        var dateRetrieved = DateTime.UtcNow;
                        docnode.SetAttributeValue("date-retrieved", dateRetrieved.ToString("o"));
                        html.OwnerDocument.SetPageUrl((info != null ? info.RespondingUrl : null) ?? url);

#if WEBCLIENT
                        foreach(string key in response.Headers.Keys)
                        {    
                            var val = response.Headers.GetValues(key).FirstOrDefault();
                            
#else
                        foreach (var header in response.Headers.Union(response.Content.Headers))
                        {
                            var key = header.Key;
                            var val = header.Value.FirstOrDefault();
                            
#endif
                            
                            if (key == "Set-Cookie") continue;

                            if (val != null)
                            {
                                docnode.SetAttributeValue("header-" + key, val);

                                if (cacheData != null) cacheData.Headers[key] = val;

                            }
                        
                        }

                        if (cacheData != null) cacheData.DateRetrieved = dateRetrieved;

                    }
                    finally
                    {
                        if (lazy != null) lazy.Dispose();
                        lazy = null;
                    }
                });


                // if (redirectLocation == null) html = (await response.Content.ReadAsStringAsync()).AsHtmlDocumentNode();

            }
            return html;
        }

        internal class HttpRequestMessageBox : IDisposable
        {
            public HttpRequestMessage Message;

           public void Dispose()
            {
                if (Message != null)
                {
#if !WEBCLIENT
                    Message.Dispose();
#endif
                    Message = null;
                }
            }
        }


#if STANDALONE

        public static Uri AsUri(this string url)
        {
            return new Uri(url);
        }
        public static Uri AsUri(this string url, bool allowNull)
        {
            return url != null ? new Uri(url) : null;
        }

#endif

        public static LazyUri AsLazyUri(this string url)
        {
            return new LazyUri(url);
        }
        private async static Task<HtmlNode> GetHtmlNodeAsyncImpl2(this LazyUri lazyurl, WebRequestOptions preprocessedOptions,
#if NET35
        IDictionary<string, string> metaParameters,
#else
         IReadOnlyDictionary<string, string> metaParameters,
#endif
          bool hasExtraOptions, Credentials credentialsForVary)
        {
            var cachePath = Caching.GetWebCachePath(HttpUtils.GetVaryUrl(lazyurl, metaParameters, credentialsForVary), hasExtraOptions, true);
            var originalLazy = lazyurl;
#if !STANDALONE
            if (lazyurl.IsHostedOn("proxy.shaman.io"))
            {
                if (lazyurl.Host == "bingcache.proxy.shaman.io")
                {
                    var original = lazyurl.PathAndQuery.Substring(1).AsUri();
                    var results = await ObjectManager.GetEntities<Shaman.Connectors.Bing.WebResult>().RemoteSearch("\"" + original.ToString() + "\"").GetFirstPageAsync();
                    if (results != null)
                    {
                        var p = results.FirstOrDefault();
                        if (p.Url == original && p.CacheUrl != null)
                        {
                            return await GetHtmlNodeAsync(p.CacheUrl);
                        }
                    }
                }
                throw new Exception("Unsupported shaman proxy.");
            }
#endif

            if (metaParameters != null && metaParameters.TryGetValue("$js") == "1")
            {
                var fragment = lazyurl.Fragment;
                var firstMeta = fragment.IndexOf("&$");

                var fragmentPos = lazyurl.AbsoluteUri.IndexOf("#");

                lazyurl = new LazyUri(lazyurl.AbsoluteUri.Substring(0, fragmentPos + (firstMeta != -1 ? firstMeta : 0)));

                HtmlNode node = null;

                await Utils.CheckLocalFileAccessAsync(lazyurl);
#if DESKTOP
                if (cachePath != null)
                {
                    var data = Caching.TryReadCacheFile(cachePath);
                    if (data != null && (data.ExceptionType == null || !Caching.IgnoreCachedFailedRequests))
                    {
                        Utils.RaiseWebRequestEvent(lazyurl, true);

                        if (data.ExceptionType != null) throw Caching.RebuildException(data, lazyurl);
                        var jsexecResults = data.JsExecutionResults != null ? JsonConvert.DeserializeObject<PageExecutionResults>(data.JsExecutionResults) : null;
                        node = data.RecreateNode(lazyurl, preprocessedOptions, cachePath);
                        node.OwnerDocument.Tag = jsexecResults;
                    }
                }
#endif
                if (node == null)
                {
#if DESKTOP
                    try
                    {
                        var r = await HttpUtils.GetJavascriptProcessedPageAsync(originalLazy, lazyurl, metaParameters);
                        if (preprocessedOptions != null) preprocessedOptions.PageExecutionResults = r;
                        node = r.GetHtmlNode();
                        EnsurePageConstraints(node, metaParameters);
                        if (cachePath != null)
                        {
                            Caching.SaveCache(cachePath, new WebCache()
                            {
                                Url = lazyurl,
                                Result = node.OwnerDocument.WriteTo(),
                                JsExecutionResults = JsonConvert.SerializeObject(r, Formatting.None)
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        if (cachePath != null)
                        {
                            var t = Caching.GetWebCacheForException(ex, lazyurl, -1);
                            Caching.SaveCache(cachePath, t);
                        }
                        throw;
                    }
#else
                    throw new NotSupportedException("JavaScript preprocessing not available on this platform.");
#endif
                }

                MaybeKeepReturnedPage(lazyurl, node);
                node.OwnerDocument.DocumentNode.SetAttributeValue("requested-url", originalLazy.AbsoluteUri);
                return node;
            }
            else
            {

#if DESKTOP
                var p = Caching.TryReadFromCache(cachePath, lazyurl, preprocessedOptions);
                if (p != null)
                {
                    await TaskEx.Yield();
                    return p;
                }
#endif
                var numRedirects = 0;
                var hasProcessedFormButton = false;
                lazyurl = lazyurl.Clone();
                while (true)
                {
                    var result = await GetHtmlOrJsonAsync(lazyurl, preprocessedOptions, metaParameters, credentialsForVary, cachePath != null || Caching.IsWebCacheEnabled());
                    if (result.Exception != null)
                    {
#if DESKTOP
                        Caching.SaveCache(cachePath, result.CacheData);
#endif
                        throw result.Exception;
                    }
                    var page = result.Node;
                    var redirectLocation = result.RedirectUrl;
                    if (page != null)
                    {

                        if (!hasProcessedFormButton)
                        {
                            var formButtonName = metaParameters.TryGetValue("$formbutton");
                            var formButtonSelector = metaParameters.TryGetValue("$formbuttonsel");
                            if (formButtonSelector != null || formButtonName != null)
                            {
                                HtmlNode button;
                                if (formButtonSelector != null)
                                {
                                    throw new ArgumentException("$formbuttonsel is only allowed when $js is set to 1. Use $formbutton instead (with the name of the button instead of its selector)");
                                }
                                else if (formButtonName != null)
                                {
                                    button = page.DescendantsAndSelf().FirstOrDefault(x => x.Id == formButtonName || x.GetAttributeValue("name") == formButtonName);
                                    if (button == null) throw new ExtractionException(message: "No element has the name or ID specified by the $formbutton metaparameter.");
                                }
                                else
                                {
                                    throw Sanity.ShouldntHaveHappened();
                                }

                                if (metaParameters.Any(x => x.Key.StartsWith("$formsel-"))) throw new ArgumentException("$formsel-* metaparameters are only allowed when $js is set to 1. Use $form-* instead (with the name of the field instead of its selector)");



                                var parameters = metaParameters.Where(x => x.Key.StartsWith("$form-")).Select(x => new KeyValuePair<string, string>(x.Key.Substring(6), x.Value)).ToList();
                                lazyurl = HttpUtils.SetUpOptionsFromFormButton(button, preprocessedOptions, parameters);
                                var preserve = metaParameters.Where(x => x.Key == "$forbid-selector" || x.Key == "$assert-selector" || x.Key == "$error-selector" || x.Key == "$error-status-selector").ToList();
                                metaParameters = ProcessMetaParameters(lazyurl, preprocessedOptions);
                                if (preserve.Any())
                                {
                                    var m = metaParameters.ToDictionary(x => x.Key, x => x.Value);
                                    foreach (var item in preserve)
                                    {
                                        m[item.Key] = item.Value;
                                    }
                                    metaParameters = m;
                                }
                                continue;
                            }

                        }

                        EnsurePageConstraints(page, metaParameters);
                        MaybeKeepReturnedPage(lazyurl, page);
                    }

                    if (redirectLocation == null)
                    {
                        //var html = page.ChildNodes.FirstOrDefault(x => x.Name == "html");
                        //var head = (html ?? page).ChildNodes.FirstOrDefault(x => x.Name == "head");
                        var metaRedirect = page.Descendants().FirstOrDefault(x => x.TagName == "meta" && string.Equals(x.GetAttributeValue("http-equiv"), "refresh", StringComparison.OrdinalIgnoreCase));
                        if (metaRedirect != null)
                        {
                            var value = metaRedirect.GetAttributeValue("content");
                            if (value != null)
                            {
                                var urlString = value.TryCapture(@"(?:url|URL|Url)\s*=\s*[\'""]?(.+?)[\'""]?\s*\;?\s*$");
                                if (urlString != null)
                                {
                                    var time = value.TryCapture(@"^\s*(\d+)[\s\,\;]");
                                    if (time == null || int.Parse(time) <= 10)
                                        redirectLocation = new Uri(lazyurl.PathConsistentUrl, urlString);
                                }
                            }
                        }
                    }

                    if (redirectLocation != null)
                    {
                        if (metaParameters.TryGetValue("$noredir") == "1")
                        {
                            page.OwnerDocument.DocumentNode.SetAttributeValue("redirect-url", redirectLocation.AbsoluteUri);
                        }
                        else
                        {
                            if (!preprocessedOptions.AllowRedirects)
                                throw new WebException("An unexpected redirect was received from the server.", HttpUtils.UnexpectedRedirect);
                            numRedirects++;
                            if (numRedirects >= 5) throw new WebException("The maximum number of http-equiv redirects has been reached.", HttpUtils.MaximumNumberOfRedirectsExceeded);

                            preprocessedOptions.PostData = null;
                            preprocessedOptions.PostString = null;
                            lazyurl = new LazyUri(redirectLocation);
                            continue;
                        }
                    }

                    page.OwnerDocument.DocumentNode.SetAttributeValue("requested-url", originalLazy.AbsoluteUri);

#if DESKTOP
                    Caching.SaveCache(cachePath, result.CacheData);
#endif

                    return page;
                }
            }

        }


        public static string GetQueryParameter(this Uri url, string name)
        {
            return HttpUtils.GetParameters(url.Query).FirstOrDefault(x => x.Key == name).Value;
        }

        public static IEnumerable<KeyValuePair<string, string>> GetQueryParameters(this Uri url)
        {
            return HttpUtils.GetParameters(url.Query);
        }

        public static IEnumerable<KeyValuePair<string, string>> GetFragmentParameters(this Uri url)
        {
            return HttpUtils.GetParameters(url.Fragment);
        }



#if STANDALONE
        internal static void MaybeKeepReturnedPage(LazyUri url, HtmlNode node)
        {

        }
#endif
        private async static Task<HtmlNode> GetHtmlNodeAsyncImpl(this LazyUri url, WebRequestOptions preprocessedOptions,
#if NET35
        IDictionary<string, string> metaParameters,
#else 
        IReadOnlyDictionary<string, string> metaParameters,
#endif
        bool hasExtraOptions, Credentials credentialsForVary)
        {
#if !STANDALONE
            using (var timing = Timing.Create(Timing.TimingCategory.Http, url))
#endif
            {


                var t = 10;
                while (true)
                {
                    try
                    {
                        var p = await GetHtmlNodeAsyncImpl2(url, preprocessedOptions, metaParameters, hasExtraOptions, credentialsForVary);
                        foreach (var cookie in preprocessedOptions.CookiesList)
                        {
                            p.OwnerDocument.DocumentNode.SetAttributeValue("cookie-" + cookie.Name, cookie.Value);
                        }
                        if (p.OwnerDocument.DocumentNode.GetAttributeValue("from-cache") != "1")
                        {
#if !STANDALONE
                            if (SuccessfulWebRequestCounter != null)
                            {
                                SuccessfulWebRequestCounter.OnEvent();
                            }
#endif
                        }
#if !STANDALONE
                        timing.Complete();
#endif
                        return p;
                    }
                    catch (Exception) when (KeepRetryingFailedRequests)
                    {
#if DESKTOP
                        var c = Caching.GetWebCachePath(url, hasExtraOptions, false);
                        if (c != null)
                        {
                            File.Delete(c);
                        }
#endif
                    }
                    t *= 2;
                    await TaskEx.Delay(TimeSpan.FromSeconds(10));
                }
            }
        }

        public static bool KeepRetryingFailedRequests {[RestrictedAccess] get;[RestrictedAccess] set; }

#if STANDALONE
        public static Task<HtmlNode> GetHtmlNodeAsync(this Uri url, WebRequestOptions options = null, Runtime.CookieContainer cookieContainer = null)
#else
        public static Task<HtmlNode> GetHtmlNodeAsync(this Uri url, WebRequestOptions options, Runtime.CookieContainer cookieContainer)        
#endif
        {
            return GetHtmlNodeAsync(new LazyUri(url), options, cookieContainer);
        }

#if STANDALONE
        public static async Task<HtmlNode> GetHtmlNodeAsync(this LazyUri url, WebRequestOptions options = null, Runtime.CookieContainer cookieContainer = null)
#else
        public static async Task<HtmlNode> GetHtmlNodeAsync(this LazyUri url, WebRequestOptions options, Runtime.CookieContainer cookieContainer)
#endif
        {
            var hasExtraOptions = options != null;
            if (options == null) options = new WebRequestOptions();

            var metaParameters = ProcessMetaParameters(url, options) ?? new Dictionary<string, string>();

            string siteIdentifier = null;

            Credentials credentials = null;

            var isolated = cookieContainer as IsolatedCookieContainer;
            if (isolated != null)
            {
                foreach (var c in isolated._cookies)
                {
                    options.AddCookie(c.Key, c.Value, PriorityCookie.PRIORITY_IsolatedCookieContainerInitial);
                }
                if (isolated.CacheVaryKey != null) url.AppendFragmentParameter("$varyisolatedcookies", isolated.CacheVaryKey);
                else hasExtraOptions = true;
                var page = await GetHtmlNodeAsyncImpl(url, options, metaParameters, hasExtraOptions, credentials);
                isolated._cookies = options.CookiesList.ToDictionary(x => x.Name, x => x.Value);
                isolated.MaybeSave();
                return page;
            }

#if !STANDALONE
            var siteInfo = cookieContainer as SiteInfo;
            if (siteInfo != null)
            {
                siteIdentifier = siteInfo.SiteIdentifier;
                credentials = ObjectManager.GetCredentials(siteIdentifier);

                if (credentials.LastCookies != null)
                {
                    foreach (var c in Utils.GetParameters(credentials.LastCookies))
                    {
                        options.AddCookie(c.Key, c.Value, PriorityCookie.PRIORITY_SiteInfoInitial);
                    }
                }


                try
                {
                    var oldcookies = options.CookiesList.ToDictionary(x => x.Name, x => x.Value);
                    var page = await GetHtmlNodeAsyncImpl(url, options, metaParameters, hasExtraOptions, credentials);
                    VerifyAuthentication(page, siteInfo, url, siteIdentifier);


                    credentials.LastCookies = Utils.ParametersToString(options.CookiesList.Select(x => new KeyValuePair<string, string>(x.Name, x.Value)));
                    var newcookies = options.CookiesList.ToDictionary(x => x.Name, x => x.Value);
                    if (siteInfo.HasSessionIdChanged(oldcookies, newcookies))
                    {

                        var task = siteInfo.OnSessionCreatedAsync(newcookies);
                        if (task != null) await task;
                        credentials.LastLoginDate = DateTime.UtcNow;
                        if (!ObjectManager.InMemory)
                            ObjectManager.SaveCredentials(credentials);
                    }


                    return page;

                }
                catch (WebException ex)
                {
                    var s = ex.GetResponseStatusCode();
                    Caching.SetDummyResponseWithUrl(ex, url, s);
                    if (s != HttpStatusCode.Forbidden && s != HttpStatusCode.Unauthorized) throw;
                }
                catch (WebsiteAuthenticationException)
                {
                }







                var username = credentials.UserName;
                if (username == null && siteInfo.RequiresCredentials) throw new WebsiteAuthenticationException("No username has been provided.") { SiteInfo = siteInfo, SiteIdentifier = siteIdentifier };
                var password = credentials.Password;
                var returnedCookies = await siteInfo.LoginAsync(url.Url, username, password);


                foreach (var item in returnedCookies)
                {
                    options.AddCookie(item.Key, item.Value, PriorityCookie.PRIORITY_Login);
                }


                credentials.LastCookies = Utils.ParametersToString(options.CookiesList.Select(x => new KeyValuePair<string, string>(x.Name, x.Value)));
                credentials.LastLoginDate = DateTime.UtcNow;

                if (!ObjectManager.InMemory)
                    ObjectManager.SaveCredentials(credentials);




                var task2 = siteInfo.OnSessionCreatedAsync(options.CookiesList.ToDictionary(x => x.Name, x => x.Value));
                if (task2 != null) await task2;

#if DESKTOP
                var path = Caching.GetWebCachePath(HttpUtils.GetVaryUrl(url, metaParameters, credentials), hasExtraOptions, false);
                if (path != null) File.Delete(path);
#endif

            }

#endif
            {
                try
                {
                    var page = await GetHtmlNodeAsyncImpl(url, options, metaParameters, hasExtraOptions, credentials);
#if !STANDALONE
                    VerifyAuthentication(page, siteInfo, url, siteIdentifier);
#endif
                    return page;
                }
                catch (WebException ex)
                {
                    var s = ex.GetResponseStatusCode();
                    Caching.SetDummyResponseWithUrl(ex, url, s);
#if STANDALONE
                    throw;
#else
                    if (s == HttpStatusCode.Forbidden || s == HttpStatusCode.Unauthorized)
                    {
                        var wea = new WebsiteAuthenticationException("The server returned: " + s);
                        wea.RequestedUrl = url.Url;
                        wea.SiteIdentifier = siteIdentifier;
                        wea.SiteInfo = siteInfo;
                        throw wea;
                    }
                    else throw;
#endif
                }
            }
        }


        public static async Task<HttpResponseMessage> GetResponseAsync(this LazyUri url, WebRequestOptions options = null)
        {
            if (options == null) options = new WebRequestOptions();
            var metaparameters = ProcessMetaParameters(url, options);
            var r = await url.SendAsync(options, null);
            if (r.Exception != null) throw r.Exception;
#if NET35
            if( r.Response.StatusCode < (HttpStatusCode)200 || r.Response.StatusCode > (HttpStatusCode)299) {
                throw new WebException("The server returned: " + r.Response.StatusCode, null, WebExceptionStatus.ProtocolError, r.Response);
            }
#else
            r.Response.EnsureSuccessStatusCode();
#endif

#if NET35
            var length = r.Response.ContentLength;
#else
            var length = r.Response.Content.Headers.ContentLength.GetValueOrDefault(-1);
#endif
            if (length != -1)
            {
                var forbiddenSizes = metaparameters.TryGetValue("$forbid-size");
                if (forbiddenSizes != null)
                {
                    foreach (var item in forbiddenSizes.SplitFast(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var interval = item.SplitFast('-', StringSplitOptions.None);
                        if (interval.Length == 1)
                        {
                            if (length != long.Parse(interval[0])) continue;
                        }
                        else if (interval.Length == 2)
                        {
                            if (interval[0].Length != 0 && length < long.Parse(interval[0])) continue;
                            if (interval[1].Length != 0 && length > long.Parse(interval[1])) continue;
                        }
                        throw new WebException("Invalid file length.");
                    }
                }
            }
            return r.Response;
        }


        /// <summary>
        /// Gets the HTTP status code from a <see cref="WebException"/>.
        /// </summary>
        /// <param name="ex"></param>
        /// <returns></returns>
        [AllowNumericLiterals]
        public static HttpStatusCode GetResponseStatusCode(this WebException ex)
        {
            var response = ex.Response as HttpWebResponse;
            if (response != null) return response.StatusCode;

            var converted = (int)ex.Status;
            if (converted >= 100 && converted <= 999) return (HttpStatusCode)converted;

            return default(HttpStatusCode);
        }

#endif


        public static bool IsHostedOn(this Uri url, string baseHost)
        {

            return IsHostedOn(url.Host, baseHost);
        }
#if !SALTARELLE
        public static bool IsHostedOn(this LazyUri url, string baseHost)
        {
            return IsHostedOn(url.Host, baseHost);
        }

#endif
        private static bool IsHostedOn(this string fullHost, string baseHost)
        {

            if (baseHost.Length > fullHost.Length) return false;
            if (baseHost.Length == fullHost.Length)
                return string.Equals(baseHost, fullHost,
#if SALTARELLE
 true);
#else
 StringComparison.OrdinalIgnoreCase);
#endif

            var k = fullHost[fullHost.Length - baseHost.Length - 1];

            if (k == '.')
                return
#if SALTARELLE
 fullHost.ToLower().EndsWith(baseHost.ToLower());
#else
 fullHost.EndsWith(baseHost, StringComparison.OrdinalIgnoreCase);
#endif
            else return false;

        }
                public static bool IsPlainText(this HtmlDocument document)
        {
            return document.DocumentNode.GetAttributeValue("plain-text") == "1";
        }
#if !SALTARELLE
        /// <summary>
        /// Returns the value of the specified query attribute.
        /// </summary>
        /// <param name="url">The url.</param>
        /// <param name="name">The key of the attribute.</param>
        /// <returns>The value of the attribute, or null if it is not found.</returns>
        /// <example>new Uri("http://example.com/index?page=3&amp;view=summary").GetParameter("page");</example>
        public static string GetParameter(this Uri url, string name)
        {
            var attr = url.GetParametersEnumerable().FirstOrDefault(x => x.Key == name);
            // It's a struct - won't be null
            return attr.Value;
        }
        public static Uri GetLeftPartPathUri(this Uri url, int count)
        {
            var z = GetLeftPartPath(url, count);
            return (url.GetLeftPart(UriPartial.Authority) + z).AsUri();
        }

        public static string GetLeftPartPath(this Uri url, int count)
        {
            var s = url.AbsolutePath.AsValueString().Split('/');
            var sb = ReseekableStringBuilder.AcquirePooledStringBuilder();
            for (int i = 0; i < count + 1; i++)
            {
                if (i != 0) sb.Append('/');
                sb.AppendValueString(s[i]);
            }
            return ReseekableStringBuilder.GetValueAndRelease(sb);
        }
        public static string GetPathComponent(this Uri url, int index)
        {
            var s = url.AbsolutePath.AsValueString().Split('/');
            if (s.Length <= index + 1) return null;
            return s[index + 1].ToClrString();
        }


        public static Task<T> WithTimeout<T>(this Task<T> task, TimeSpan timeout)
        {
            return (Task<T>)WithTimeoutInternal<T>(task, timeout, true);
        }


        public static Task WithTimeout(this Task task, TimeSpan timeout)
        {
            return WithTimeoutInternal<bool>(task, timeout, false);
        }

        public static CancellableTimout CancelAfter<T>(this TaskCompletionSource<T> tcs, TimeSpan timeout)
        {
            var t = CancellableTimout.ScheduleUnsafe(() =>
            {
                tcs.TrySetException(new TimeoutException());
            }, timeout);
            tcs.Task.ContinueWith((s) =>
            {
                t.Dispose();
            });
            return t;
        }

        private static Task WithTimeoutInternal<T>(Task task, TimeSpan timeout, bool hasResult)
        {
            var tcs = new TaskCompletionSource<T>();
            tcs.CancelAfter(timeout);

            task.ContinueWith(originalTask =>
            {
                if (originalTask.IsFaulted) tcs.TrySetException(originalTask.Exception);
                else if (originalTask.IsCanceled) tcs.TrySetCanceled();
                else
                {
                    if (hasResult) tcs.TrySetResult(((Task<T>)task).Result);
                    else tcs.TrySetResult(default(T));
                }
            });

            return tcs.Task;
        }


        public static string GetLeftPart_UriPartial_Query(this Uri url)
        {
            var b = url.GetLeftPart(UriPartial.Path);
            if (b == null) return null;
            return b + url.Query;
        }

        /// <summary>
        /// Returns the query attributes.
        /// </summary>
        public static IDictionary<string, string> GetParameters(this Uri url)
        {
            var dict = new Dictionary<string, string>();
            foreach (var item in url.GetParametersEnumerable())
            {
                dict[item.Key] = item.Value;
            }
            return dict;
        }

        /// <summary>
        /// Returns the query attributes.
        /// </summary>
        private static IEnumerable<KeyValuePair<string, string>> GetParametersEnumerable(this Uri url)
        {
            if (url == null) throw new ArgumentNullException();
            var query = url.Query;
            if (string.IsNullOrEmpty(query)) return Enumerable.Empty<KeyValuePair<string, string>>();
            return HttpUtils.GetParameters(query);
        }
#endif


        public static Uri GetPageUrl(this HtmlDocument document)
        {
            var u = document.DocumentNode.GetAttributeValue("document-url");
            return u != null ? u.AsUri() : null;
        }

        public static Uri GetBaseUrl(this HtmlDocument document)
        {
            var b = document.DocumentNode.GetAttributeValue("base-url");
            if (b == null)
            {
                var attrval = document.DocumentNode.TryGetValue("base[href]", "href");
                if (attrval != null)
                {
                    try
                    {
                        b = HttpUtils.GetAbsoluteUrlInternal(document.GetPageUrl(), attrval).AbsoluteUri;
                    }
                    catch
                    {
                    }
                }
                if (b == null) b = string.Empty;
                document.DocumentNode.SetAttributeValue("base-url", b);
            }
            if (b.Length == 0) return document.GetPageUrl();
            return b.AsUri();
        }


        public static HtmlNode NextSibling(this HtmlNode node, string nodeName)
        {

            while (true)
            {
                var sib = node.NextSibling;
                if (sib == null) return null;
                if (sib.NodeType == HtmlNodeType.Element && sib.TagName == nodeName) return sib;
                node = sib;
            }

        }


        public static bool IsHeading(this Shaman.Dom.HtmlNode node)
        {
            if (node == null) throw new ArgumentNullException();
            var name = node.TagName;
            if (name.Length != 2) return false;
            if (name[0] != 'h') return false;
#if SALTARELLE
            if (!AwdeeUtils.IsDigit(name[1])) return false;
#else
            if (!char.IsDigit(name[1])) return false;
#endif
            return true;
        }

        public static string GetIntuitiveXPath(this HtmlNode node)
        {
            if (node.TagName == "#document") return "(document)";

            var parents = new List<HtmlNode>();
            var p = node;
            while (p.ParentNode != null && p.ParentNode.NodeType != HtmlNodeType.Document)
            {
                parents.Add(p);
                p = p.ParentNode;
            }
            var first = true;
            parents.Reverse();
            var sb = ReseekableStringBuilder.AcquirePooledStringBuilder();
            foreach (var item in parents)
            {
                if (!first)
                    sb.Append(" > ");

                first = false;
                var id = item.Id;

                sb.Append(item.TagName);

                if (!string.IsNullOrEmpty(id))
                {
                    sb.Append('#');
                    sb.Append(id);
                }
                else
                {
                    foreach (var name in item.ClassList.Take(5))
                    {
                        sb.Append('.');
                        sb.Append(name.TrimSize(10, 13, true));
                    }
                }

            }
            return ReseekableStringBuilder.GetValueAndRelease(sb);

        }

        public static HtmlNode AsHtmlDocumentNode(this string html)
        {
            var d = new HtmlDocument();
            d.LoadHtml(html);
            return d.DocumentNode;
        }

        public static HtmlNode AsHtmlNode(this string html)
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(html);
            if (doc.DocumentNode.ChildNodes.Count == 1) return doc.DocumentNode.FirstChild;
            return doc.DocumentNode;
        }


        public static Uri GetImageUrl(this HtmlNode finalNode)
        {
            Uri url = null;
            foreach (var attr in finalNode.Attributes)
            {
                var name = attr.Name;
                if (name.Contains("src") || name.Contains("lazy") || name.Contains("img") || name.Contains("original") || name.StartsWith("data-"))
                {
                    var str = attr.Value;
                    if (string.IsNullOrEmpty(str)) continue;

                    if (name != "src")
                    {
                        if (str[0] == '{' || str[0] == '[' || str[0] == '<') continue;
                    }
                    Uri u = null;
                    if (name.StartsWith("data-") && !(str.StartsWith("http://") || str.StartsWith("https://") || str.StartsWith("/"))) continue;
                    try
                    {
                        u = HttpUtils.GetAbsoluteUri(finalNode.OwnerDocument.GetBaseUrl(), str);
                        if (name != "src")
                        {
                            var path = u.AbsolutePath.ToLowerFast();
                            if (!(path.EndsWith(".jpg") ||
                                path.EndsWith(".jpeg") ||
                                path.EndsWith(".png")) ||
                                path.EndsWith(".gif"))
                                continue;
                        }
                    }
                    catch
                    {
                        continue;
                    }
                    if (u != null)
                    {
                        if (u.Scheme != HttpUtils.UriSchemeHttp && u.Scheme != HttpUtils.UriSchemeHttps) u = null;
                        else
                        {
                            if (name != "src") return u;
                            url = u;
                        }
                    }


                }
            }
            return url;
        }




        public static void WriteHtmlEncoded(this TextWriter sb, string text, bool newLinesToBr = false, int startIndex = 0, int endIndex = -1)
        {
            if (text == null) return;
            if (endIndex == -1) endIndex = text.Length;
            if (startIndex == endIndex)
            {
                return;
            }

            for (int i = startIndex; i < endIndex; i++)
            {
                char c = text[i];

                if (c == '&')
                {
                    sb.Write("&amp;");
                }
                else if (c == '<')
                {
                    sb.Write("&lt;");
                }
                else if (c == '>')
                {
                    sb.Write("&gt;");
                }
                else if (c == '"')
                {
                    sb.Write("&quot;");
                }
                else if (c == '\'')
                {
                    sb.Write("&apos;");
                }
                else if (c == '\n' && newLinesToBr)
                {
                    sb.Write("<br>\n");
                }
                else
                {
                    sb.Write(c);
                }
            }
        }


        public static void MakeAbsoluteUrls(this HtmlNode node)
        {
            MakeAbsoluteUrlsInternal(node, node.OwnerDocument.GetBaseUrl());
        }




        internal static void MakeAbsoluteUrlsInternal(this HtmlNode node, Uri baseUrl)
        {
            try
            {
                HttpUtils.MakeAbsoluteAttribute(node, "href", baseUrl);
                HttpUtils.MakeAbsoluteAttribute(node, "src", baseUrl);
                HttpUtils.MakeAbsoluteAttribute(node, "action", baseUrl);
            }
            catch
            {
#if !SMALL_LIB_AWDEE
                LogNonCriticalException(ex, baseUrl, node);
#endif
            }

            if (!node.HasChildNodes) return;

            foreach (var subnode in node.ChildNodes)
            {
                MakeAbsoluteUrlsInternal(subnode, baseUrl);
            }
        }

        public static Uri TryGetLinkUrl(this HtmlNode node)
        {
            if (node == null) throw new ArgumentNullException();
            var baseUrl = node.OwnerDocument.GetBaseUrl();
            var href = node.GetAttributeValue("href");
            if (href != null)
            {
                return HttpUtils.GetAbsoluteUrlInternal(baseUrl, href);
            }

            var src = node.GetAttributeValue("src");
            if (src != null)
            {
                return HttpUtils.GetAbsoluteUrlInternal(baseUrl, src);
            }
            return null;
        }

        public static Uri TryGetLinkUrl(this HtmlNode node, string selector)
        {
            var subnode = node.FindSingle(selector);
            if (subnode == null) return null;
            return TryGetLinkUrl(subnode);
        }

        public static Uri GetLinkUrl(this HtmlNode node)
        {
            var result = TryGetLinkUrl(node);
            if (result == null) throw new UnparsableDataException() { SourceDataNode = node };
            return result;
        }

        public static Uri GetLinkUrl(this HtmlNode node, string selector)
        {
            var result = TryGetLinkUrl(node, selector);
            if (result == null) throw new UnparsableDataException(nodeQuery: selector) { SourceDataNode = node };
            return result;
        }

        private enum TextStatus
        {
            Start,
            MustInsertSpaceBeforeNextVisibleChar,
            MustInsertNewLineBeforeNextVisibleChar,
            LastCharWasVisible
        }


        public static string GetText(this HtmlNode node)
        {

            if (node == null) throw new ArgumentNullException();


            var sb = ReseekableStringBuilder.AcquirePooledStringBuilder();

            TextStatus status = TextStatus.Start;
            AppendText(node, sb, ref status);
            TrimLastWhitespaceCharacters(sb);
            var s = sb.ToFinalString();
            ReseekableStringBuilder.Release(sb);
            return s;
        }



        public static string GetFirstLevelText(this HtmlNode node, bool includeLinks = false, bool includeBold = true)
        {
            return node.ChildNodes.Where(delegate (HtmlNode child)
            {
                if (child.NodeType == HtmlNodeType.Text) return true;
                var name = child.TagName;
                if (name == "br") return true;
                if (name == "p") return true;

                if (name == "a" && includeLinks) return true;
                if (name == "b" && includeBold) return true;
                if (name == "strong" && includeBold) return true;

                return false;
            }).GetText();
        }

        public static string GetText(this IEnumerable<HtmlNode> nodes)
        {

            if (nodes == null) throw new ArgumentNullException();

            var sb = ReseekableStringBuilder.AcquirePooledStringBuilder();

            TextStatus status = TextStatus.Start;

            foreach (var node in nodes)
            {
                if (ShouldIgnoreNonFirstLevelNodeForInnerText(node)) continue;
                AppendText(node, sb, ref status);
            }
            TrimLastWhitespaceCharacters(sb);
            return sb.ToFinalString();
        }

        private static bool ShouldIgnoreNonFirstLevelNodeForInnerText(HtmlNode node)
        {
            return node.TagName == "script" || node.TagName == "style" || node.NodeType == HtmlNodeType.Comment;
        }

        private static string ToFinalString(this StringBuilder s)
        {
            var length = s.Length;
            if (s.Length == 0) return null;
#if SALTARELLE
            return s.ToString();
#else
            return s.SubstringCached(0);
#endif
        }

        private static void TrimLastWhitespaceCharacters(StringBuilder sb)
        {
#if SALTARELLE
            var s = sb.ToString();
            var length = s.Length;
            for (int i = length - 1; i >= 0; i--)
            {
                var ch = s[i];
                if (ch == ' ' || ch == '\t' || ch == '\r' || ch == '\n' || ch == '\xA0') length--;
                else break;
            }
            if (length != s.Length)
            {
                sb.Clear();
                sb.Append(s.Substring(0, length));
            }
#else
            var initialLength = sb.Length;
            for (int i = initialLength - 1; i >= 0; i--)
            {
                var ch = sb[i];
                if (ch == ' ' || ch == '\t' || ch == '\r' || ch == '\n' || ch == '\xA0') sb.Length--;
                else break;
            }
#endif
        }

        private static void AppendText(this HtmlNode node, StringBuilder sb, ref TextStatus status)
        {
            var textNode = node as HtmlTextNode;

            if (textNode != null)
            {
                var internalText = textNode.Text;
                for (int i = 0; i < internalText.Length; i++)
                {
                    var ch = internalText[i];
                    if (ch == ' ' || ch == '\t' || ch == '\r' || ch == '\n')
                    {
                        if (status == TextStatus.LastCharWasVisible)
                        {
                            status = TextStatus.MustInsertSpaceBeforeNextVisibleChar;
                        }
                    }
                    else
                    {
                        if (status == TextStatus.MustInsertNewLineBeforeNextVisibleChar) OnNewLine(sb, ref status);
                        else if (status == TextStatus.MustInsertSpaceBeforeNextVisibleChar) sb.Append(' ');

                        sb.Append(ch);
                        status = TextStatus.LastCharWasVisible;
                    }
                }
                return;
            }
            var isDisplayBlock = Configuration_DisplayBlockElements.Contains(node.TagName);

            if (status != TextStatus.Start)
            {

                if (isDisplayBlock)
                {
                    status = TextStatus.MustInsertNewLineBeforeNextVisibleChar;
                }
                else if (status == TextStatus.LastCharWasVisible)
                {
                    if (node.TagName == "td")
                    {
                        status = TextStatus.MustInsertSpaceBeforeNextVisibleChar;
                    }
                }
            }

            if (node.TagName == "br" || node.TagName == "p")
            {
                OnNewLine(sb, ref status);
            }


            if (node.HasChildNodes)
            {
                foreach (var subnode in node.ChildNodes)
                {
                    if (ShouldIgnoreNonFirstLevelNodeForInnerText(subnode)) continue;
                    subnode.AppendText(sb, ref status);
                }

                if (node.TagName == "p") //End of tag
                {
                    OnNewLine(sb, ref status);
                }

            }

            if (isDisplayBlock && status != TextStatus.Start)
            {
                status = TextStatus.MustInsertNewLineBeforeNextVisibleChar;
            }




        }

#if !SALTARELLE
        [Configuration]
#endif
        private readonly static string[] Configuration_DisplayBlockElements = new[] { "div", "article", "li", "ul" };

        private static void OnNewLine(StringBuilder sb, ref TextStatus status)
        {
            switch (status)
            {
                case TextStatus.Start:
                    // Do nothing
                    break;
                case TextStatus.MustInsertSpaceBeforeNextVisibleChar:
                    status = TextStatus.MustInsertNewLineBeforeNextVisibleChar;
                    break;
                case TextStatus.MustInsertNewLineBeforeNextVisibleChar:
                    sb.Append('\n');
                    break;
                case TextStatus.LastCharWasVisible:
                    status = TextStatus.MustInsertNewLineBeforeNextVisibleChar;
                    break;
            }

        }





        public static IEnumerable<HtmlNode> FindAll(this HtmlNode context, string selector)
        {
            if (selector == null || context == null) throw new ArgumentNullException();
            return context.QuerySelectorAll(selector);
        }


        public static HtmlNode FindSingle(this HtmlNode context, string selector)
        {
            if (selector == null || context == null) throw new ArgumentNullException();
            return context.QuerySelector(selector);
        }

#if !SALTARELLE

        private static void EnsurePageConstraints(HtmlNode node,
#if NET35
        IDictionary<string, string> metaParameters)
#else
        IReadOnlyDictionary<string, string> metaParameters)
#endif
        {
            if (metaParameters != null)
            {
                var mustHave = metaParameters.TryGetValue("$assert-selector");
                if (mustHave != null && node.FindSingle(mustHave) == null) throw new WebException("The retrieved page does not contain an element that matches " + mustHave);

                var mustForbit = metaParameters.TryGetValue("$forbid-selector");
                if (mustForbit != null && node.FindSingle(mustForbit) != null) throw new WebException("The retrieved page contains an element that matches the forbidden selector " + mustForbit);

                var errorSelector = metaParameters.TryGetValue("$error-selector");
                var error = errorSelector != null ? node.TryGetValue(errorSelector) : null;
                if (error != null) throw new WebException("The page reports: " + error);
            }
        }
#endif

        public static string TrimSize(this string str, int size)
        {
            if (str == null) throw new ArgumentNullException();
            if (size < 1) throw new ArgumentException();
            if (str.Length > size) return str.Substring(0, size);
            else return str;
        }

        public static string TrimSize(this string str, int size, int allowedExtraChars, bool hypens)
        {
            if (str == null) throw new ArgumentNullException();
            if (size < 1) throw new ArgumentException();
            if (allowedExtraChars < 0) throw new ArgumentException();
            if (str.Length > size + allowedExtraChars)
            {
                str = str.Substring(0, size);
                if (hypens) str += "…";
                return str;
            }
            else
            {
                return str;
            }
        }

#if !SALTARELLE
        [RestrictedAccess]
        public static Task<T> GetJsonAsync<T>(this Uri url, WebRequestOptions options = null)
        {
            return new LazyUri(url).GetJsonAsync<T>(options);
        }
        [RestrictedAccess]
        public async static Task<T> GetJsonAsync<T>(this LazyUri url, WebRequestOptions options = null)
        {
            var str = await url.GetStringAsync(options);
            if (typeof(JToken).GetTypeInfo().IsAssignableFrom(typeof(T).GetTypeInfo())) return (T)(object)JToken.Load(HttpUtils.CreateJsonReader(new StringReader(str)));
            return JsonConvert.DeserializeObject<T>(str);
        }


        public static Task<string> GetStringAsync(this Uri url, WebRequestOptions options = null)
        {
            return new LazyUri(url).GetStringAsync(options);
        }

        public async static Task<string> GetStringAsync(this LazyUri url, WebRequestOptions options = null)
        {
            var p = url.AbsoluteUri;
            var q = "$assume-text=1";
            if (string.IsNullOrEmpty(url.Fragment)) p += "#" + q;
            else p += "&" + q;

            return (await p.AsLazyUri().GetHtmlNodeAsync(options)).OwnerDocument.GetPlainText();
        }

        [RestrictedAccess]
        public static Task<HttpResponseMessage> GetAsync(this Uri url, WebRequestOptions options = null)
        {
            var u = new LazyUri(url);
            return u.GetResponseAsync(options);
        }

        public static async Task<Stream> GetStreamAsync(this Uri url, WebRequestOptions options = null)
        {
            var u = new LazyUri(url);
            var response = await u.GetResponseAsync(options);
#if NET35
            return response.GetResponseStream();
#else
            return await response.Content.ReadAsStreamAsync();
#endif
        }


        internal static 
#if NET35
        IDictionary<string, string>
#else
        IReadOnlyDictionary<string, string>
#endif
        ProcessMetaParameters(LazyUri url, WebRequestOptions options)
        {
            Dictionary<string, JObject> jsonPostObjects = null;
            JObject jsonPostSingleJson = null;
            var metaParameters = url.FragmentParameters.Where(x => x.Key.StartsWith("$")).ToDictionary();
            if (metaParameters.Any())
            {


                if (metaParameters.TryGetValue("$js") == "1")
                {
                    return metaParameters;
                }
                else
                {

                    if (options == null) options = new WebRequestOptions();
                    foreach (var item in metaParameters)
                    {
                        var key = item.Key;
                        if (key == "$method")
                        {
                            options.Method = item.Value;
                        }
                        else if (key.StartsWith("$cookie-"))
                        {
                            options.AddCookie(key.Substring(8), item.Value, PriorityCookie.PRIORITY_MetaParameter);
                        }
                        else if (key.StartsWith("$post-"))
                        {
                            options.AddPostField(key.Substring(6), item.Value);
                        }
                        else if (key == "$post")
                        {
                            options.PostString = item.Value;
                        }
                        else if (key.StartsWith("$json-post-"))
                        {
                            if (jsonPostObjects == null) jsonPostObjects = new Dictionary<string, JObject>();
                            AddJsonPartialField(jsonPostObjects, key.Substring(11), item.Value);
                        }
                        else if (key.StartsWith("$json-post"))
                        {
                            if (jsonPostSingleJson == null) jsonPostSingleJson = new JObject();
                            SetJsonMetaparameter(jsonPostSingleJson, key.Substring(11), item.Value);
                        }
                        else if (key.StartsWith("$header-"))
                        {
                            var name = key.Substring(8);
                            if (name == "User-Agent") options.UserAgent = item.Value;
                            else if (name == "Referer")
                            {
                                options.Referrer = HttpUtils.GetAbsoluteUriAsString(url.Url, item.Value).AsUri();
                            }
                            else options.AddHeader(name, item.Value);
                        }
                        else if (key == "$allow-redir")
                        {
                            if (item.Value == "0") options.AllowRedirects = false;
                        }
                        else if (key == "$timeout")
                        {
                            options.Timeout = int.Parse(item.Value);
                            options.TimeoutSecondRetrialAfterError = null; // options.Timeout;
                            options.TimeoutStartSecondRetrial = null;
                        }
                        else if (key == "$waitbefore")
                        {
                            options.WaitBefore = TimeSpan.FromMilliseconds(int.Parse(item.Value));
                        }
                    }
                    if (jsonPostObjects != null)
                    {
                        foreach (var item in jsonPostObjects)
                        {
                            options.AddPostField(item.Key, item.Value.ToString(Newtonsoft.Json.Formatting.None));
                        }
                    }
                    if (jsonPostSingleJson != null)
                    {
                        options.PostString = jsonPostSingleJson.ToString(Newtonsoft.Json.Formatting.None);
                    }
                }
            }
            return metaParameters;

        }



        private static void AddJsonPartialField(Dictionary<string, JObject> dict, string path, string value)
        {

            var idx = path.IndexOf('.');
            if (idx == -1) throw new FormatException("Invalid JSON metaparameter.");

            var key = path.Substring(0, idx);
            var obj = dict.TryGetValue(key);
            if (obj == null)
            {
                obj = new JObject();
                dict[key] = obj;
            }
            SetJsonMetaparameter(obj, path.Substring(idx + 1), value);

        }

        private static void SetJsonMetaparameter(JObject obj, string path, string value)
        {
            var idx = path.IndexOf('.');

            if (idx != -1)
            {
                var key = path.Substring(0, idx);
                var sub = obj.TryGetJToken(key);
                var subobj = sub as JObject;
                if (subobj == null)
                {
                    if (sub == null)
                    {
                        subobj = new JObject();
                        obj[key] = subobj;
                    }
                    else
                    {
                        throw new FormatException("Duplicate JSON metaparameter.");
                    }
                }
                SetJsonMetaparameter(subobj, path.Substring(idx + 1), value);
            }
            else
            {
                var literal = path.EndsWith("~");
                if (literal) path = path.Substring(0, path.Length - 1);
                var sub = obj.TryGetJToken(path);
                if (sub != null) throw new FormatException("Duplicate JSON metaparameter.");
                JValue actualValue = literal ? (JValue)JValue.Load(HttpUtils.CreateJsonReader(value)) : new JValue(value);
                obj[path] = actualValue;
            }

        }

#endif



        public static void SetPageUrl(this HtmlDocument document, Uri url)
        {
            if (url == null)
                document.DocumentNode.Attributes.Remove("document-url");
            else
                document.DocumentNode.SetAttributeValue("document-url", url.AbsoluteUri);
        }
#if !SALTARELLE
        public static void SetPageUrl(this HtmlDocument document, LazyUri url)
        {
            if (url == null)
                document.DocumentNode.Attributes.Remove("document-url");
            else
                document.DocumentNode.SetAttributeValue("document-url", url.AbsoluteUri);
        }
#endif



        internal static Dictionary<string, string> mimeToExtension;
        internal static Dictionary<string, string> extensionToMime;
        internal static void InitializeMimesDictionary()
        {
            if (mimeToExtension == null)
            {
                lock (typeof(HttpExtensionMethods))
                {
                    if (mimeToExtension == null)
                    {
                        mimeToExtension = new Dictionary<string, string>();
                        extensionToMime = new Dictionary<string, string>();

                        AddMimeExtensionCouple("application/fractals", ".fif");
                        AddMimeExtensionCouple("application/futuresplash", ".spl");
                        AddMimeExtensionCouple("application/hta", ".hta");
                        AddMimeExtensionCouple("application/mac-binhex40", ".hqx");
                        AddMimeExtensionCouple("application/ms-vsi", ".vsi");
                        AddMimeExtensionCouple("application/msaccess", ".accdb");
                        AddMimeExtensionCouple("application/msaccess.AddMimeExtensionCouplein", ".accda");
                        AddMimeExtensionCouple("application/msaccess.cab", ".accdc");
                        AddMimeExtensionCouple("application/msaccess.exec", ".accde");
                        AddMimeExtensionCouple("application/msaccess.ftemplate", ".accft");
                        AddMimeExtensionCouple("application/msaccess.runtime", ".accdr");
                        AddMimeExtensionCouple("application/msaccess.template", ".accdt");
                        AddMimeExtensionCouple("application/msaccess.webapplication", ".accdw");
                        AddMimeExtensionCouple("application/msonenote", ".one");
                        AddMimeExtensionCouple("application/msword", ".doc");
                        AddMimeExtensionCouple("application/opensearchdescription+xml", ".osdx");
                        AddMimeExtensionCouple("application/oxps", ".oxps");
                        AddMimeExtensionCouple("application/pdf", ".pdf");
                        AddMimeExtensionCouple("application/pkcs10", ".p10");
                        AddMimeExtensionCouple("application/pkcs7-mime", ".p7c");
                        AddMimeExtensionCouple("application/pkcs7-signature", ".p7s");
                        AddMimeExtensionCouple("application/pkix-cert", ".cer");
                        AddMimeExtensionCouple("application/pkix-crl", ".crl");
                        AddMimeExtensionCouple("application/postscript", ".ps");
                        AddMimeExtensionCouple("application/vnd.ms-excel", ".xls");
                        AddMimeExtensionCouple("application/vnd.ms-excel.12", ".xlsx");
                        AddMimeExtensionCouple("application/vnd.ms-excel.AddMimeExtensionCouplein.macroEnabled.12", ".xlam");
                        AddMimeExtensionCouple("application/vnd.ms-excel.sheet.binary.macroEnabled.12", ".xlsb");
                        AddMimeExtensionCouple("application/vnd.ms-excel.sheet.macroEnabled.12", ".xlsm");
                        AddMimeExtensionCouple("application/vnd.ms-excel.template.macroEnabled.12", ".xltm");
                        AddMimeExtensionCouple("application/vnd.ms-officetheme", ".thmx");
                        AddMimeExtensionCouple("application/vnd.ms-pki.certstore", ".sst");
                        AddMimeExtensionCouple("application/vnd.ms-pki.pko", ".pko");
                        AddMimeExtensionCouple("application/vnd.ms-pki.seccat", ".cat");
                        AddMimeExtensionCouple("application/vnd.ms-pki.stl", ".stl");
                        AddMimeExtensionCouple("application/vnd.ms-powerpoint", ".ppt");
                        AddMimeExtensionCouple("application/vnd.ms-powerpoint.12", ".pptx");
                        AddMimeExtensionCouple("application/vnd.ms-powerpoint.AddMimeExtensionCouplein.macroEnabled.12", ".ppam");
                        AddMimeExtensionCouple("application/vnd.ms-powerpoint.presentation.macroEnabled.12", ".pptm");
                        AddMimeExtensionCouple("application/vnd.ms-powerpoint.slide.macroEnabled.12", ".sldm");
                        AddMimeExtensionCouple("application/vnd.ms-powerpoint.slideshow.macroEnabled.12", ".ppsm");
                        AddMimeExtensionCouple("application/vnd.ms-powerpoint.template.macroEnabled.12", ".potm");
                        AddMimeExtensionCouple("application/vnd.ms-publisher", ".pub");
                        AddMimeExtensionCouple("application/vnd.ms-visio.viewer", ".vdx");
                        AddMimeExtensionCouple("application/vnd.ms-word.document.12", ".docx");
                        AddMimeExtensionCouple("application/vnd.ms-word.document.macroEnabled.12", ".docm");
                        AddMimeExtensionCouple("application/vnd.ms-word.template.12", ".dotx");
                        AddMimeExtensionCouple("application/vnd.ms-word.template.macroEnabled.12", ".dotm");
                        AddMimeExtensionCouple("application/vnd.ms-wpl", ".wpl");
                        AddMimeExtensionCouple("application/vnd.ms-xpsdocument", ".xps");
                        AddMimeExtensionCouple("application/vnd.oasis.opendocument.presentation", ".odp");
                        AddMimeExtensionCouple("application/vnd.oasis.opendocument.spreadsheet", ".ods");
                        AddMimeExtensionCouple("application/vnd.oasis.opendocument.text", ".odt");
                        AddMimeExtensionCouple("application/vnd.openxmlformats-officedocument.presentationml.presentation", ".pptx");
                        AddMimeExtensionCouple("application/vnd.openxmlformats-officedocument.presentationml.slide", ".sldx");
                        AddMimeExtensionCouple("application/vnd.openxmlformats-officedocument.presentationml.slideshow", ".ppsx");
                        AddMimeExtensionCouple("application/vnd.openxmlformats-officedocument.presentationml.template", ".potx");
                        AddMimeExtensionCouple("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ".xlsx");
                        AddMimeExtensionCouple("application/vnd.openxmlformats-officedocument.spreadsheetml.template", ".xltx");
                        AddMimeExtensionCouple("application/vnd.openxmlformats-officedocument.wordprocessingml.document", ".docx");
                        AddMimeExtensionCouple("application/vnd.openxmlformats-officedocument.wordprocessingml.template", ".dotx");
                        AddMimeExtensionCouple("application/x-bittorrent", ".torrent");
                        AddMimeExtensionCouple("application/x-bittorrent-app", ".btapp");
                        AddMimeExtensionCouple("application/x-bittorrent-appinst", ".btinstall");
                        AddMimeExtensionCouple("application/x-bittorrent-key", ".btkey");
                        AddMimeExtensionCouple("application/x-bittorrent-skin", ".btskin");
                        AddMimeExtensionCouple("application/x-bittorrentsearchdescription+xml", ".btsearch");
                        AddMimeExtensionCouple("application/x-bridge-url", ".adobebridge");
                        AddMimeExtensionCouple("application/x-compress", ".z");
                        AddMimeExtensionCouple("application/x-compressed", ".tgz");
                        AddMimeExtensionCouple("application/x-gzip", ".gz");
                        AddMimeExtensionCouple("application/x-java-jnlp-file", ".jnlp");
                        AddMimeExtensionCouple("application/x-jtx+xps", ".jtx");
                        AddMimeExtensionCouple("application/x-latex", ".latex");
                        AddMimeExtensionCouple("application/x-mix-transfer", ".nix");
                        AddMimeExtensionCouple("application/x-mplayer2", ".asx");
                        AddMimeExtensionCouple("application/x-ms-application", ".application");
                        AddMimeExtensionCouple("application/x-ms-vsto", ".vsto");
                        AddMimeExtensionCouple("application/x-ms-wmd", ".wmd");
                        AddMimeExtensionCouple("application/x-ms-wmz", ".wmz");
                        AddMimeExtensionCouple("application/x-ms-xbap", ".xbap");
                        AddMimeExtensionCouple("application/x-mswebsite", ".website");
                        AddMimeExtensionCouple("application/x-pkcs12", ".p12");
                        AddMimeExtensionCouple("application/x-pkcs7-certificates", ".p7b");
                        AddMimeExtensionCouple("application/x-pkcs7-certreqresp", ".p7r");
                        AddMimeExtensionCouple("application/x-shockwave-flash", ".swf");
                        AddMimeExtensionCouple("application/x-stuffit", ".sit");
                        AddMimeExtensionCouple("application/x-tar", ".tar");
                        AddMimeExtensionCouple("application/x-troff-man", ".man");
                        AddMimeExtensionCouple("application/x-wmplayer", ".asx");
                        AddMimeExtensionCouple("application/x-x509-ca-cert", ".cer");
                        AddMimeExtensionCouple("application/x-zip-compressed", ".zip");
                        AddMimeExtensionCouple("application/xaml+xml", ".xaml");
                        AddMimeExtensionCouple("application/xhtml+xml", ".xht");
                        AddMimeExtensionCouple("application/xml", ".xml");
                        AddMimeExtensionCouple("application/xps", ".xps");
                        AddMimeExtensionCouple("application/zip", ".zip");
                        AddMimeExtensionCouple("audio/3gpp", ".3gp");
                        AddMimeExtensionCouple("audio/3gpp2", ".3g2");
                        AddMimeExtensionCouple("audio/aiff", ".aiff");
                        AddMimeExtensionCouple("audio/basic", ".au");
                        AddMimeExtensionCouple("audio/ec3", ".ec3");
                        AddMimeExtensionCouple("audio/mid", ".mid");
                        AddMimeExtensionCouple("audio/midi", ".mid");
                        AddMimeExtensionCouple("audio/mp3", ".mp3");
                        AddMimeExtensionCouple("audio/mp4", ".m4a");
                        AddMimeExtensionCouple("audio/mpeg", ".mp3");
                        AddMimeExtensionCouple("audio/mpegurl", ".m3u");
                        AddMimeExtensionCouple("audio/mpg", ".mp3");
                        AddMimeExtensionCouple("audio/vnd.dlna.adts", ".adts");
                        AddMimeExtensionCouple("audio/vnd.dolby.dd-raw", ".ac3");
                        AddMimeExtensionCouple("audio/wav", ".wav");
                        AddMimeExtensionCouple("audio/x-aiff", ".aiff");
                        AddMimeExtensionCouple("audio/x-mid", ".mid");
                        AddMimeExtensionCouple("audio/x-midi", ".mid");
                        AddMimeExtensionCouple("audio/x-mp3", ".mp3");
                        AddMimeExtensionCouple("audio/x-mpeg", ".mp3");
                        AddMimeExtensionCouple("audio/x-mpegurl", ".m3u");
                        AddMimeExtensionCouple("audio/x-mpg", ".mp3");
                        AddMimeExtensionCouple("audio/x-ms-wax", ".wax");
                        AddMimeExtensionCouple("audio/x-ms-wma", ".wma");
                        AddMimeExtensionCouple("audio/x-wav", ".wav");
                        AddMimeExtensionCouple("image/bmp", ".bmp");
                        AddMimeExtensionCouple("image/gif", ".gif");
                        AddMimeExtensionCouple("image/jpeg", ".jpeg");
                        AddMimeExtensionCouple("image/jpeg", ".jpg");
                        AddMimeExtensionCouple("image/pjpeg", ".jpg");
                        AddMimeExtensionCouple("image/png", ".png");
                        AddMimeExtensionCouple("image/svg+xml", ".svg");
                        AddMimeExtensionCouple("image/tiff", ".tiff");
                        AddMimeExtensionCouple("image/vnd.ms-photo", ".wdp");
                        AddMimeExtensionCouple("image/x-emf", ".emf");
                        AddMimeExtensionCouple("image/x-icon", ".ico");
                        AddMimeExtensionCouple("image/x-png", ".png");
                        AddMimeExtensionCouple("image/x-wmf", ".wmf");
                        AddMimeExtensionCouple("image/webp", ".webp");
                        AddMimeExtensionCouple("image/jxr", ".wdp");
                        AddMimeExtensionCouple("image/jxr", ".hdp");
                        AddMimeExtensionCouple("image/jxr", ".jxr");
                        AddMimeExtensionCouple("midi/mid", ".mid");
                        AddMimeExtensionCouple("model/vnd.dwfx+xps", ".dwfx");
                        AddMimeExtensionCouple("model/vnd.easmx+xps", ".easmx");
                        AddMimeExtensionCouple("model/vnd.edrwx+xps", ".edrwx");
                        AddMimeExtensionCouple("model/vnd.eprtx+xps", ".eprtx");
                        AddMimeExtensionCouple("pkcs10", ".p10");
                        AddMimeExtensionCouple("pkcs7-mime", ".p7m");
                        AddMimeExtensionCouple("pkcs7-signature", ".p7s");
                        AddMimeExtensionCouple("pkix-cert", ".cer");
                        AddMimeExtensionCouple("pkix-crl", ".crl");
                        AddMimeExtensionCouple("text/calendar", ".ics");
                        AddMimeExtensionCouple("text/css", ".css");
                        AddMimeExtensionCouple("text/directory", ".vcf");
                        AddMimeExtensionCouple("text/directory;profile=vCard", ".vcf");
                        AddMimeExtensionCouple("text/html", ".htm");
                        AddMimeExtensionCouple("text/plain", ".sql");
                        AddMimeExtensionCouple("text/scriptlet", ".wsc");
                        AddMimeExtensionCouple("text/vcard", ".vcf");
                        AddMimeExtensionCouple("text/x-component", ".htc");
                        AddMimeExtensionCouple("text/x-ms-contact", ".contact");
                        AddMimeExtensionCouple("text/x-ms-iqy", ".iqy");
                        AddMimeExtensionCouple("text/x-ms-odc", ".odc");
                        AddMimeExtensionCouple("text/x-ms-rqy", ".rqy");
                        AddMimeExtensionCouple("text/x-vcard", ".vcf");
                        AddMimeExtensionCouple("text/xml", ".xml");
                        AddMimeExtensionCouple("video/3gpp", ".3gp");
                        AddMimeExtensionCouple("video/3gpp2", ".3g2");
                        AddMimeExtensionCouple("video/avi", ".avi");
                        AddMimeExtensionCouple("video/mp4", ".mp4");
                        AddMimeExtensionCouple("video/mpeg", ".mpeg");
                        AddMimeExtensionCouple("video/mpg", ".mpeg");
                        AddMimeExtensionCouple("video/msvideo", ".avi");
                        AddMimeExtensionCouple("video/quicktime", ".mov");
                        AddMimeExtensionCouple("video/vnd.dlna.mpeg-tts", ".tts");
                        AddMimeExtensionCouple("video/wtv", ".wtv");
                        AddMimeExtensionCouple("video/x-mpeg", ".mpeg");
                        AddMimeExtensionCouple("video/x-mpeg2a", ".mpeg");
                        AddMimeExtensionCouple("video/x-ms-asf", ".asx");
                        AddMimeExtensionCouple("video/x-ms-asf-plugin", ".asx");
                        AddMimeExtensionCouple("video/x-ms-dvr", ".dvr-ms");
                        AddMimeExtensionCouple("video/x-ms-wm", ".wm");
                        AddMimeExtensionCouple("video/x-ms-wmv", ".wmv");
                        AddMimeExtensionCouple("video/x-ms-wmx", ".wmx");
                        AddMimeExtensionCouple("video/x-ms-wvx", ".wvx");
                        AddMimeExtensionCouple("video/x-msvideo", ".avi");
                        AddMimeExtensionCouple("video/webm", ".webm");
                        AddMimeExtensionCouple("vnd.ms-pki.certstore", ".sst");
                        AddMimeExtensionCouple("vnd.ms-pki.pko", ".pko");
                        AddMimeExtensionCouple("vnd.ms-pki.seccat", ".cat");
                        AddMimeExtensionCouple("vnd.ms-pki.stl", ".stl");
                        AddMimeExtensionCouple("x-pkcs12", ".p12");
                        AddMimeExtensionCouple("x-pkcs7-certificates", ".p7b");
                        AddMimeExtensionCouple("x-pkcs7-certreqresp", ".p7r");
                        AddMimeExtensionCouple("x-x509-ca-cert", ".cer");

                    }
                }
            }
        }

        private static void AddMimeExtensionCouple(string mime, string extension)
        {
            mimeToExtension[mime] = extension;
            var old = extensionToMime.TryGetValue(extension);
            if (old == null || old.Length >= mime.Length) extensionToMime[extension] = mime;
        }



    }
}