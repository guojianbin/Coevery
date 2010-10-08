﻿using System;
using System.IO;
using System.Web;
using System.Web.Mvc;
using System.Web.UI;
using Autofac;
using Orchard.DisplayManagement;
using Orchard.DisplayManagement.Shapes;
using Orchard.Localization;
using Orchard.Mvc.Html;
using Orchard.Mvc.Spooling;
using Orchard.Security;
using Orchard.Security.Permissions;
using Orchard.UI.Resources;
using TagBuilder = System.Web.Mvc.TagBuilder;

namespace Orchard.Mvc {
    public class ViewPage<TModel> : System.Web.Mvc.ViewPage<TModel>, IOrchardViewPage {
        private ScriptRegister _scriptRegister;
        private ResourceRegister _stylesheetRegister;

        private object _display;
        private Localizer _localizer = NullLocalizer.Instance;
        private object _layout;
        private WorkContext _workContext;

        public Localizer T { get { return _localizer; } }
        public dynamic Display { get { return _display; } }
        public ScriptRegister Script {
            get {
                return _scriptRegister ??
                    (_scriptRegister = new ViewPageScriptRegister(Writer, Html.ViewDataContainer, Html.Resolve<IResourceManager>()));
            }
        }

        public dynamic Layout { get { return _layout; } }
        public WorkContext WorkContext { get { return _workContext; } }

        public IDisplayHelperFactory DisplayHelperFactory { get; set; }

        public IAuthorizer Authorizer { get; set; }
        

        public ResourceRegister Style {
            get {
                return _stylesheetRegister ??
                    (_stylesheetRegister = new ResourceRegister(Html.ViewDataContainer, Html.Resolve<IResourceManager>(), "stylesheet"));
            }
        }
        
	    public override void InitHelpers() {
            base.InitHelpers();

            _workContext = ViewContext.GetWorkContext();
            _workContext.Resolve<IComponentContext>().InjectUnsetProperties(this);

            _localizer = LocalizationUtilities.Resolve(ViewContext, AppRelativeVirtualPath);
            _display = DisplayHelperFactory.CreateHelper(ViewContext, this);
            _layout = _workContext.Layout;
        }

        public virtual void RegisterLink(LinkEntry link) {
            Html.Resolve<IResourceManager>().RegisterLink(link);
        }

        public void SetMeta(string name, string content) {
            SetMeta(new MetaEntry { Name = name, Content = content });
        }

        public virtual void SetMeta(MetaEntry meta) {
            Html.Resolve<IResourceManager>().SetMeta(meta);
        }
        
        public void AppendMeta(string name, string content, string contentSeparator) {
            AppendMeta(new MetaEntry { Name = name, Content = content }, contentSeparator);
        }        

        public virtual void AppendMeta(MetaEntry meta, string contentSeparator) {
            Html.Resolve<IResourceManager>().AppendMeta(meta, contentSeparator);
        }
                
        public MvcHtmlString H(string value) {
            return MvcHtmlString.Create(Html.Encode(value));
        }

        public bool AuthorizedFor(Permission permission) {
            return Authorizer.Authorize(permission);
        }

        public bool HasText(object thing) {
            return !string.IsNullOrWhiteSpace(thing as string);
        }

        public TagBuilder Tag(dynamic shape, string tagName) {
            return Html.Resolve<ITagBuilderFactory>().Create(shape, tagName);
        }

        public IHtmlString DisplayChildren(dynamic shape) {
            var writer = new HtmlStringWriter();
            foreach (var item in shape) {
                writer.Write(Display(item));
            }
            return writer;
        }

        public IDisposable Capture(Action<IHtmlString> callback) {
            return new CaptureScope(Writer, callback);
        }

        public class CaptureScope : IDisposable {
            private readonly HtmlTextWriter _context;
            private readonly Action<IHtmlString> _callback;
            private readonly TextWriter _oldWriter;
            private readonly HtmlStringWriter _writer;

            public CaptureScope(HtmlTextWriter context, Action<IHtmlString> callback) {
                _context = context;
                _oldWriter = _context.InnerWriter;
                _callback = callback;
                _context.InnerWriter = _writer = new HtmlStringWriter();
            }

            public void Dispose() {
                _callback(_writer);
                _context.InnerWriter = _oldWriter;
            }
        }

        internal class ViewPageScriptRegister : ScriptRegister {
            private readonly HtmlTextWriter _context;

            public ViewPageScriptRegister(HtmlTextWriter context, IViewDataContainer container, IResourceManager resourceManager)
                : base(container, resourceManager) {
                _context = context;
            }

            public override IDisposable Head() {
                return new CaptureScope(_context, s => ResourceManager.RegisterHeadScript(s.ToString()));
            }

            public override IDisposable Foot() {
                return new CaptureScope(_context, s => ResourceManager.RegisterFootScript(s.ToString()));
            }
        }
    }

    public class ViewPage : ViewPage<dynamic> {
    }
}