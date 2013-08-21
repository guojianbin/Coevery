﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Coevery.Taxonomies.Fields;
using Coevery.Taxonomies.Models;
using Orchard;
using Orchard.Autoroute.Models;
using Orchard.ContentManagement;
using Orchard.ContentManagement.Aspects;
using Orchard.ContentManagement.MetaData;
using Orchard.Core.Common.Models;
using Orchard.Core.Title.Models;
using Orchard.Data;
using Orchard.Localization;
using Orchard.Logging;
using Orchard.Security;
using Orchard.UI.Notify;
using Orchard.Utility.Extensions;

namespace Coevery.Taxonomies.Services {
    public class TaxonomyService : ITaxonomyService {
        private readonly IRepository<TermContentItem> _termContentItemRepository;
        private readonly IContentManager _contentManager;
        private readonly INotifier _notifier;
        private readonly IAuthorizationService _authorizationService;
        private readonly IContentDefinitionManager _contentDefinitionManager;
        private readonly IOrchardServices _services;

        public TaxonomyService(
            IRepository<TermContentItem> termContentItemRepository,
            IContentManager contentManager,
            INotifier notifier,
            IContentDefinitionManager contentDefinitionManager,
            IAuthorizationService authorizationService,
            IOrchardServices services) {
            _termContentItemRepository = termContentItemRepository;
            _contentManager = contentManager;
            _notifier = notifier;
            _authorizationService = authorizationService;
            _contentDefinitionManager = contentDefinitionManager;
            _services = services;

            Logger = NullLogger.Instance;
            T = NullLocalizer.Instance;
        }

        public ILogger Logger { get; set; }
        public Localizer T { get; set; }

        public IEnumerable<TaxonomyPart> GetTaxonomies() {
            return _contentManager.Query<TaxonomyPart, TaxonomyPartRecord>().WithQueryHints(new QueryHints().ExpandParts<AutoroutePart, TitlePart>()).List();
        }

        public TaxonomyPart GetTaxonomy(int id) {
            return _contentManager.Get(id, VersionOptions.Published, new QueryHints().ExpandParts<TaxonomyPart, AutoroutePart, TitlePart>()).As<TaxonomyPart>();
        }

        public TaxonomyPart GetTaxonomyByName(string name) {
            if (String.IsNullOrWhiteSpace(name)) {
                throw new ArgumentNullException("name");
            }

            return _contentManager
                .Query<TaxonomyPart>()
                .Join<TitlePartRecord>()
                .Where(r => r.Title == name)
                .WithQueryHints(new QueryHints().ExpandRecords<AutoroutePartRecord, CommonPartRecord>())
                .List()
                .FirstOrDefault();
        }

        public TaxonomyPart GetTaxonomyBySlug(string slug) {
            if (String.IsNullOrWhiteSpace(slug)) {
                throw new ArgumentNullException("slug");
            }

            return _contentManager
                .Query<TaxonomyPart, TaxonomyPartRecord>()
                .Join<TitlePartRecord>()
                .Join<AutoroutePartRecord>()
                .Where(r => r.DisplayAlias == slug)
                .List()
                .FirstOrDefault();
        }

        public void CreateTermContentType(TaxonomyPart taxonomy) {
            // create the associated term's content type
            taxonomy.TermTypeName = GenerateTermTypeName(taxonomy.Name);

            _contentDefinitionManager.AlterTypeDefinition(taxonomy.TermTypeName, 
                cfg => cfg
                    .WithSetting("Taxonomy", taxonomy.Name)
                    .WithPart("TermPart")
                    .WithPart("TitlePart")
                    .WithPart("AutoroutePart", builder => builder
                        .WithSetting("AutorouteSettings.AllowCustomPattern", "true")
                        .WithSetting("AutorouteSettings.AutomaticAdjustmentOnEdit", "false")
                        .WithSetting("AutorouteSettings.PatternDefinitions", "[{Name:'Taxonomy and Title', Pattern: '{Content.Container.Path}/{Content.Slug}', Description: 'my-taxonomy/my-term/sub-term'}]")
                        .WithSetting("AutorouteSettings.DefaultPatternIndex", "0"))
                    .WithPart("CommonPart")
                    .DisplayedAs(taxonomy.Name + " Term")
                );

        }

        public void DeleteTaxonomy(TaxonomyPart taxonomy) {
            _contentManager.Remove(taxonomy.ContentItem);

            // Removing terms
            foreach (var term in GetTerms(taxonomy.Id)) {
                DeleteTerm(term);
            }

            _contentDefinitionManager.DeleteTypeDefinition(taxonomy.TermTypeName);
        }

        public string GenerateTermTypeName(string taxonomyName) {
            var name = taxonomyName.ToSafeName() + "Term";
            int i = 2;
            while (_contentDefinitionManager.GetTypeDefinition(name) != null) {
                name = taxonomyName.ToSafeName() + i++;
            }

            return name;
        }

        public bool GenerateTermsFromImport(int taxonomyId, string terms) {
            var taxonomy = GetTaxonomy(taxonomyId);

            if (taxonomy == null) {
                return false;
            }

            var topTerm = new TermPosition();

            using (var reader = new StringReader(terms)) {
                string line;
                var previousLevel = 0;
                var parents = new Stack<TermPosition>();
                TermPosition parentTerm = null;
                while (null != (line = reader.ReadLine())) {

                    // ignore empty lines
                    if (String.IsNullOrWhiteSpace(line)) {
                        continue;
                    }

                    // compute level from tabs
                    var level = 0;
                    while (line[level] == '\t') level++; // number of tabs to know the level

                    // create a new term content item
                    var term = NewTerm(taxonomy);

                    // detect parent term
                    if (level == previousLevel + 1) {
                        parentTerm = parents.Peek();
                        parents.Push(new TermPosition { Term = term });
                    }
                    else if (level == previousLevel) {
                        // same parent term
                        if (parents.Any())
                            parents.Pop();

                        parents.Push(new TermPosition { Term = term });
                    }
                    else if (level < previousLevel) {
                        for (var i = previousLevel; i >= level; i--)
                            parents.Pop();

                        parentTerm = parents.Any() ? parents.Peek() : null;
                        parents.Push(new TermPosition { Term = term });
                    }

                    // increment number of children
                    if (parentTerm == null) {
                        parentTerm = topTerm;
                    }

                    parentTerm.Position++;
                    term.Weight = 10 - parentTerm.Position;

                    term.Container = parentTerm.Term == null ? taxonomy.ContentItem : parentTerm.Term.ContentItem;

                    line = line.Trim();
                    var scIndex = line.IndexOf(';'); // seek first semi-colon to extract term and slug

                    // is there a semi-colon
                    if (scIndex != -1) {
                        term.Name = line.Substring(0, scIndex);
                        term.Slug = line.Substring(scIndex + 1);
                    }
                    else {
                        term.Name = line;
                    }

                    var existing = GetTermByName(taxonomyId, term.Name);

                    // a different term exist under the same parent term ?
                    if (existing != null && existing.Container.ContentItem.Record == term.Container.ContentItem.Record) {
                        _services.Notifier.Error(T("The term {0} already exists at this level", term.Name));
                        _services.TransactionManager.Cancel();
                        return false;
                    }

                    ProcessPath(term);
                    _contentManager.Create(term, VersionOptions.Published);

                    previousLevel = level;
                }
            }
            return true;
        }

        public TermPart NewTerm(TaxonomyPart taxonomy) {
            var term = _contentManager.New<TermPart>(taxonomy.TermTypeName);
            term.TaxonomyId = taxonomy.Id;

            return term;
        }

        public IEnumerable<TermPart> GetTerms(int taxonomyId) {
            var result = _contentManager.Query<TermPart, TermPartRecord>()
                .Where(x => x.TaxonomyId == taxonomyId)
                .WithQueryHints(new QueryHints().ExpandRecords<AutoroutePartRecord, TitlePartRecord, CommonPartRecord>())
                .List();

            return TermPart.Sort(result);
        }

        public TermPart GetTermByPath(string path) {
            return _contentManager.Query<TermPart, TermPartRecord>()
                .Join<AutoroutePartRecord>()
                .WithQueryHints(new QueryHints().ExpandRecords<TitlePartRecord, CommonPartRecord>())
                .Where(rr => rr.DisplayAlias == path)
                .List()
                .FirstOrDefault();
        }

        public IEnumerable<TermPart> GetAllTerms() {
            var result = _contentManager
                .Query<TermPart, TermPartRecord>()
                .WithQueryHints(new QueryHints().ExpandRecords<AutoroutePartRecord, TitlePartRecord, CommonPartRecord>())
                .List();
            return TermPart.Sort(result);
        }

        public TermPart GetTerm(int id) {
            return _contentManager
                .Query<TermPart, TermPartRecord>()
                .WithQueryHints(new QueryHints().ExpandRecords<AutoroutePartRecord, TitlePartRecord, CommonPartRecord>())
                .Where(x => x.Id == id).List().FirstOrDefault();
        }

        public IEnumerable<TermPart> GetTermsForContentItem(int contentItemId, string field = null) {
            return String.IsNullOrEmpty(field) 
                ? _termContentItemRepository.Fetch(x => x.TermsPartRecord.ContentItemRecord.Id == contentItemId).Select(t => GetTerm(t.TermRecord.Id))
                : _termContentItemRepository.Fetch(x => x.TermsPartRecord.Id == contentItemId && x.Field == field).Select(t => GetTerm(t.TermRecord.Id));
        }

        public TermPart GetTermByName(int taxonomyId, string name) {
            return _contentManager
                .Query<TermPart, TermPartRecord>()
                .WithQueryHints(new QueryHints().ExpandRecords<AutoroutePartRecord, TitlePartRecord, CommonPartRecord>())
                .Where(t => t.TaxonomyId == taxonomyId)
                .Join<TitlePartRecord>()
                .Where(r => r.Title == name)
                .List()
                .FirstOrDefault();
        }

        public void CreateTerm(TermPart termPart) {
            if (GetTermByName(termPart.TaxonomyId, termPart.Name) == null) {
                _authorizationService.CheckAccess(Permissions.CreateTerm, _services.WorkContext.CurrentUser, null);

                termPart.As<ICommonPart>().Container = GetTaxonomy(termPart.TaxonomyId).ContentItem;
                _contentManager.Create(termPart);
            }
            else {
                _notifier.Warning(T("The term {0} already exists in this taxonomy", termPart.Name));
            }
        }

        public void DeleteTerm(TermPart termPart) {
            _contentManager.Remove(termPart.ContentItem);

            foreach(var childTerm in GetChildren(termPart)) {
                _contentManager.Remove(childTerm.ContentItem);
            }

            // delete termContentItems
            var termContentItems = _termContentItemRepository
                .Fetch(t => t.TermRecord == termPart.Record)
                .ToList();

            foreach (var termContentItem in termContentItems) {
                _termContentItemRepository.Delete(termContentItem);
            }
        }

        public void UpdateTerms(ContentItem contentItem, IEnumerable<TermPart> terms, string field) {
            var termsPart = contentItem.As<TermsPart>();

            // removing current terms for specific field
            var fieldIndexes = termsPart.Terms
                .Where(t => t.Field == field)
                .Select((t, i) => i)
                .OrderByDescending(i => i)
                .ToList();
            
            foreach(var x in fieldIndexes) {
                termsPart.Terms.RemoveAt(x);
            }
            
            // adding new terms list
            foreach(var term in terms) {
                termsPart.Terms.Add( 
                    new TermContentItem {
                        TermsPartRecord = termsPart.Record, 
                        TermRecord = term.Record, Field = field
                    });
            }
        }

        public IContentQuery<TermsPart, TermsPartRecord> GetContentItemsQuery(TermPart term, string fieldName = null) {
            var rootPath = term.FullPath + "/";

            var query = _contentManager
                .Query<TermsPart, TermsPartRecord>()
                .WithQueryHints(new QueryHints().ExpandRecords<AutoroutePartRecord, TitlePartRecord, CommonPartRecord>());

            if (String.IsNullOrWhiteSpace(fieldName)) {
                query = query.Where(
                    tpr => tpr.Terms.Any(tr =>
                        tr.TermRecord.Id == term.Id
                        || tr.TermRecord.Path.StartsWith(rootPath)));
            } else {
                query = query.Where(
                    tpr => tpr.Terms.Any(tr =>
                        tr.Field == fieldName
                         && (tr.TermRecord.Id == term.Id || tr.TermRecord.Path.StartsWith(rootPath))));
            }

            return query;
        }
        
        public long GetContentItemsCount(TermPart term, string fieldName = null) {
            return GetContentItemsQuery(term, fieldName).Count();
        }

        public IEnumerable<IContent> GetContentItems(TermPart term, int skip = 0, int count = 0, string fieldName = null) {
            return GetContentItemsQuery(term, fieldName)
                .Join<CommonPartRecord>()
                .OrderByDescending(x => x.CreatedUtc)
                .Slice(skip, count);
        }

        public IEnumerable<TermPart> GetChildren(TermPart term) {
            var rootPath = term.FullPath + "/";

            var result = _contentManager.Query<TermPart, TermPartRecord>()
                .WithQueryHints(new QueryHints().ExpandRecords<AutoroutePartRecord, TitlePartRecord, CommonPartRecord>())
                .List()
                .Where(x => x.Path.StartsWith(rootPath));

            return TermPart.Sort(result);
        }

        public IEnumerable<TermPart> GetParents(TermPart term) {
            return term.Path.Split(new [] {'/'}, StringSplitOptions.RemoveEmptyEntries).Select(id => GetTerm(int.Parse(id)));
        }

        public IEnumerable<string> GetSlugs() {
            return _contentManager
                .Query<TaxonomyPart, TaxonomyPartRecord>()
                .WithQueryHints(new QueryHints().ExpandRecords<AutoroutePartRecord, TitlePartRecord, CommonPartRecord>())
                .List()
                .Select(t => t.Slug);
        }

        public IEnumerable<string> GetTermPaths() {
            return _contentManager
                .Query<TermPart, TermPartRecord>()
                .WithQueryHints(new QueryHints().ExpandRecords<AutoroutePartRecord, TitlePartRecord, CommonPartRecord>())
                .List()
                .Select(t => t.Slug);
        }

        public void MoveTerm(TaxonomyPart taxonomy, TermPart term, TermPart parentTerm) {
            var children = GetChildren(term);
            term.Container = parentTerm == null ? taxonomy.ContentItem : parentTerm.ContentItem;
            ProcessPath(term);

            var contentItem = _contentManager.Get(term.ContentItem.Id, VersionOptions.DraftRequired);
            _contentManager.Publish(contentItem);

            foreach (var childTerm in children) {
                ProcessPath(childTerm);

                contentItem = _contentManager.Get(childTerm.ContentItem.Id, VersionOptions.DraftRequired);
                _contentManager.Publish(contentItem);
            }
        }

        public void ProcessPath(TermPart term) {
            var parentTerm = term.Container.As<TermPart>();
            term.Path = parentTerm != null ? parentTerm.FullPath + "/": "/";
        }

        public void CreateHierarchy(IEnumerable<TermPart> terms, Action<TermPartNode, TermPartNode> append) {
            var root = new TermPartNode();
            var stack = new Stack<TermPartNode>(new [] { root } );

            foreach (var term in terms) {
                var current = CreateNode(term);
                var previous = stack.Pop();

                while (previous.Level + 1 != current.Level) {
                    previous = stack.Pop();
                }

                if (append != null) {
                    append(previous, current);
                }

                previous.Items.Add(current);
                current.Parent = previous;

                stack.Push(previous);
                stack.Push(current);
            }
        }

        private static TermPartNode CreateNode(TermPart part) {
            return new TermPartNode {
                TermPart = part,
                Level = part.Path.Count(x => x == '/')
            };
        }

        private class TermPosition {
            public TermPart Term { get; set; }
            public int Position { get; set; }
        }
    }
}
