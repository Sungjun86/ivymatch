using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace IvyMatch
{
    /// <summary>
    /// Attach next to the UIDocument (Source Asset = Dashboard.uxml). Loads the data, renders the card grid,
    /// wires filters / selection / detail sections. Element names must match CONTRACT section 6.
    /// </summary>
    public class DashboardController : MonoBehaviour
    {
        [SerializeField] UIDocument document;
        [SerializeField] VisualTreeAsset cardTemplate;
        [SerializeField] string remoteJsonUrl = "";
        [SerializeField] bool autoRefreshOnStart = true;

        const float NarrowWidth = 600f;

        SchoolRepository repo;
        SchoolDatabase db = new SchoolDatabase();
        readonly Dictionary<string, School> byId = new Dictionary<string, School>();
        readonly List<SchoolCardView> cards = new List<SchoolCardView>(); // visible cards, display order
        CardGridPresenter cardPresenter;  // builds cards once per database; filters only show/hide/reorder
        bool cardsDirty = true;            // set when the database object changes
        readonly Dictionary<RateFilter, Button> filterButtons = new Dictionary<RateFilter, Button>();

        VisualElement uiRoot;      // document.rootVisualElement
        VisualElement namedRoot;   // element named "root" (or uiRoot)
        VisualElement cardGrid;
        ScrollView cardScroll;
        Label headerSub, summaryBar, emptyState;
        DetailPanelView detail;

        RateFilter filter = RateFilter.All;
        string selectedId;

        // Search + multi-filter (CONTRACT section 11). Applied after the acceptance-rate pills.
        readonly SchoolQuery query = new SchoolQuery();
        FilterPanelView filterPanel;
        int lastMissing; // schools hidden because an active filter's field is null

        // List / map switch (CONTRACT section 12). The map shows the same filtered list as the cards.
        const string MapResourcePath = "Map/us_states";
        UsMapView mapView;
        VisualElement mapContainer, detailPanelEl;
        Button viewListButton, viewMapButton;
        bool mapMode;

        // NOTE: bound in Start (not OnEnable) because UIDocument builds its tree in its own OnEnable and the
        // component order is not guaranteed. If this GameObject is disabled and re-enabled the UIDocument
        // rebuilds its tree; re-enable the component through a scene reload in that case.
        async void Start()
        {
            try
            {
                if (!BindUi()) return;

                repo = new SchoolRepository();
                SchoolDatabase loaded = await repo.LoadAsync();
                if (this == null) return; // destroyed while loading
                ApplyDatabase(loaded);
                Render();

                if (!string.IsNullOrWhiteSpace(remoteJsonUrl) && autoRefreshOnStart)
                    await RefreshAsync();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        /// <summary>Downloads remoteJsonUrl; on success reloads and re-renders. Safe to call from a UI button.</summary>
        public async Task<bool> RefreshAsync()
        {
            try
            {
                if (repo == null || string.IsNullOrWhiteSpace(remoteJsonUrl)) return false;

                bool ok = await repo.TryRefreshAsync(remoteJsonUrl);
                if (this == null || !ok) return false;

                SchoolDatabase fresh = await repo.LoadAsync();
                if (this == null) return false;
                if (fresh == null || fresh.schools == null || fresh.schools.Count == 0) return false;

                ApplyDatabase(fresh);
                Render();
                return true;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                return false;
            }
        }

        // ------------------------------------------------------------------ binding

        /// <summary>Used by IvyMatchBootstrap when the dashboard is built in code.</summary>
        public void Configure(UIDocument doc, VisualTreeAsset card)
        {
            document = doc;
            cardTemplate = card;
        }

        bool BindUi()
        {
            // Every missing reference falls back to Resources/UI so a half-wired scene still renders.
            if (document == null) document = GetComponent<UIDocument>();
            if (document == null) document = gameObject.AddComponent<UIDocument>();
            if (document.panelSettings == null) document.panelSettings = IvyMatchBootstrap.GetPanelSettings();
            if (document.visualTreeAsset == null)
                document.visualTreeAsset = Resources.Load<VisualTreeAsset>(IvyMatchBootstrap.DashboardPath);
            if (cardTemplate == null) cardTemplate = Resources.Load<VisualTreeAsset>(IvyMatchBootstrap.CardPath);
            if (document.visualTreeAsset == null || cardTemplate == null)
            {
                Debug.LogError("[IvyMatch] DashboardController: Dashboard.uxml / SchoolCard.uxml not found in Resources/UI.");
                return false;
            }

            uiRoot = document.rootVisualElement;
            if (uiRoot == null)
            {
                Debug.LogError("[IvyMatch] DashboardController: UIDocument has no rootVisualElement (Source Asset / Panel Settings missing?).");
                return false;
            }

            namedRoot = uiRoot.Q<VisualElement>("root") ?? uiRoot;
            uiRoot.style.flexGrow = 1;

            Font font = IvyMatchBootstrap.LoadKoreanFont();
            if (font != null) uiRoot.style.unityFontDefinition = FontDefinition.FromFont(font);
            cardGrid = uiRoot.Q<VisualElement>("card-grid");
            cardScroll = uiRoot.Q<ScrollView>("card-scroll");
            headerSub = uiRoot.Q<Label>("header-sub");
            summaryBar = uiRoot.Q<Label>("summary-bar");
            emptyState = uiRoot.Q<Label>("empty-state");

            if (cardScroll != null)
            {
                cardScroll.touchScrollBehavior = ScrollView.TouchScrollBehavior.Elastic;
                cardScroll.mouseWheelScrollSize = 60f;
                cardScroll.AddManipulator(new DragScrollManipulator(cardScroll));

                // When the content shrinks (detail panel closed, fewer results, list -> map) the ScrollView can keep an
                // offset past the new end; with content shorter than the screen it then cannot be scrolled back and the
                // page looks empty. Pull the offset back into range whenever either side changes size.
                cardScroll.contentContainer.RegisterCallback<GeometryChangedEvent>(_ => ClampScrollOffset());
                cardScroll.contentViewport.RegisterCallback<GeometryChangedEvent>(_ => ClampScrollOffset());
            }

            if (cardGrid == null)
            {
                Debug.LogError("[IvyMatch] DashboardController: element 'card-grid' not found in Dashboard.uxml.");
                return false;
            }

            // Filters
            BindFilter("filter-all", RateFilter.All);
            BindFilter("filter-low", RateFilter.Low);
            BindFilter("filter-mid", RateFilter.Mid);
            BindFilter("filter-high", RateFilter.High);

            // Search + filter panel
            VisualElement searchBar = uiRoot.Q<VisualElement>("search-bar");
            if (searchBar != null)
            {
                filterPanel = new FilterPanelView(searchBar, uiRoot.Q<VisualElement>("filter-panel"),
                                                  uiRoot.Q<VisualElement>("active-filters"), query);
                filterPanel.Changed += Render;
            }
            else
            {
                Debug.LogWarning("[IvyMatch] 'search-bar' not found; search and filters are disabled.");
            }

            // List / map switch
            BindMap();

            // Detail panel
            VisualElement panelEl = uiRoot.Q<VisualElement>("detail-panel");
            detailPanelEl = panelEl;
            if (panelEl != null)
            {
                detail = new DetailPanelView(panelEl, LookupSchool);
                BindSectionButton("btn-strategy", DetailPanelView.SectionStrategy);
                BindSectionButton("btn-lookfor", DetailPanelView.SectionLookFor);
                BindSectionButton("btn-compare", DetailPanelView.SectionCompare);

                Button close = uiRoot.Q<Button>("detail-close");
                if (close != null) close.clicked += ClearSelection;
                else Debug.LogWarning("[IvyMatch] 'detail-close' not found.");
            }
            else
            {
                Debug.LogWarning("[IvyMatch] 'detail-panel' not found; selection will only highlight cards.");
            }

            // Responsive: toggle "narrow" below 600 logical px.
            uiRoot.RegisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
            return true;
        }

        void BindFilter(string elementName, RateFilter f)
        {
            Button b = uiRoot.Q<Button>(elementName);
            if (b == null)
            {
                Debug.LogWarning("[IvyMatch] Filter button '" + elementName + "' not found.");
                return;
            }
            filterButtons[f] = b;
            b.clicked += () => SetFilter(f);
        }

        void BindSectionButton(string elementName, string sectionKey)
        {
            Button b = uiRoot.Q<Button>(elementName);
            if (b == null)
            {
                Debug.LogWarning("[IvyMatch] Section button '" + elementName + "' not found.");
                return;
            }
            b.clicked += () =>
            {
                if (detail != null && detail.Current != null) detail.ShowSection(sectionKey);
            };
        }

        void OnRootGeometryChanged(GeometryChangedEvent e)
        {
            if (e.newRect.width <= 0f) return;
            bool narrow = e.newRect.width < NarrowWidth;
            uiRoot.EnableInClassList("narrow", narrow);
            if (namedRoot != uiRoot) namedRoot.EnableInClassList("narrow", narrow);
            // Phones scroll by touch: no scrollbar there. Wider screens get the slim overlay bar (Dashboard.uss).
            if (cardScroll != null)
                cardScroll.verticalScrollerVisibility = narrow ? ScrollerVisibility.Hidden : ScrollerVisibility.Auto;
        }

        // ------------------------------------------------------------------ data

        void ApplyDatabase(SchoolDatabase newDb)
        {
            if (newDb == null || newDb.schools == null) newDb = new SchoolDatabase();
            db = newDb;
            cardsDirty = true;

            byId.Clear();
            foreach (School s in db.schools)
            {
                if (s != null && !string.IsNullOrEmpty(s.id)) byId[s.id] = s;
            }
        }

        School LookupSchool(string id)
        {
            School s;
            return (id != null && byId.TryGetValue(id, out s)) ? s : null;
        }

        static float? RateOf(School s)
        {
            return s != null && s.stats != null ? s.stats.rateOverall : null;
        }

        // ------------------------------------------------------------------ rendering

        void SetFilter(RateFilter f)
        {
            filter = f;
            Render();
        }

        /// <summary>Rebuilds the grid for the current filter. Never throws on empty data.</summary>
        void Render()
        {
            // Remember the open section so it survives a re-render.
            string section = detail != null ? detail.CurrentSection : null;

            // Cards are built once per database; afterwards a render only shows/hides/reorders them.
            if (cardPresenter == null && cardGrid != null) cardPresenter = new CardGridPresenter(cardGrid, MakeCard);
            if (cardsDirty && cardPresenter != null)
            {
                cardPresenter.Rebuild(db != null ? db.schools : null);
                cardsDirty = false;
            }

            List<School> byRate = RateFilters.Apply(db != null ? db.schools : null, filter);
            QueryResult found = query.Apply(byRate, DateTime.Today);
            List<School> list = found.Items;
            lastMissing = found.MissingData;

            cards.Clear();
            if (cardPresenter != null) cards.AddRange(cardPresenter.Show(list));

            UpdateHeader();
            UpdateFilterButtons();
            UpdateViewMode(list);

            if (list.Count == 0)
            {
                UiUtil.SetText(emptyState, UiText.NoMatch);
                UiUtil.SetVisible(emptyState, true);
                UiUtil.SetText(summaryBar, UiText.SummaryNoMatch + UiText.MissingNote(lastMissing));
                selectedId = null;
                if (detail != null) detail.Hide();
                SyncCardSelection();
                return;
            }

            UiUtil.SetVisible(emptyState, false);

            UpdateSummary(list);

            // Preserve selection if the selected school is still visible, else clear it.
            if (selectedId != null)
            {
                SchoolCardView selectedCard = cards.FirstOrDefault(c => c.School.id == selectedId);
                if (selectedCard == null)
                {
                    selectedId = null;
                    if (detail != null) detail.Hide();
                }
                else
                {
                    // Re-resolve by id: the database object may have been replaced by a refresh.
                    School current = LookupSchool(selectedId) ?? selectedCard.School;
                    if (detail != null)
                    {
                        detail.Show(current);
                        detail.ShowSection(section ?? DetailPanelView.SectionStrategy);
                    }
                }
            }
            else if (detail != null)
            {
                detail.Hide();
            }

            // Cards are reused, so sync the highlight on every card (hidden ones included).
            SyncCardSelection();
            if (mapView != null && mapMode) mapView.SetSelected(selectedId);
        }

        void SyncCardSelection()
        {
            if (cardPresenter == null) return;
            foreach (SchoolCardView c in cardPresenter.AllViews)
            {
                bool on = selectedId != null && c.School.id == selectedId;
                if (c.IsSelected != on) c.SetSelected(on);
            }
        }

        /// <summary>CardGridPresenter factory: instantiates one card template (not yet added to the grid).</summary>
        SchoolCardView MakeCard(School school, out VisualElement wrapper)
        {
            wrapper = null;
            try
            {
                // Instantiate() returns a TemplateContainer wrapping the UXML root.
                TemplateContainer container = cardTemplate.Instantiate();
                VisualElement cardRoot = container.Q<VisualElement>("card-root");
                if (cardRoot == null && container.childCount > 0) cardRoot = container[0];
                if (cardRoot == null)
                {
                    Debug.LogWarning("[IvyMatch] SchoolCard.uxml has no element named 'card-root'.");
                    return null;
                }

                // Dashboard.uss styles the TemplateContainer wrapper as the flex item
                // (".card-grid > TemplateContainer"), so the presenter adds the wrapper itself to the grid.
                wrapper = container;
                return new SchoolCardView(cardRoot, school, OnCardClicked);
            }
            catch (Exception e)
            {
                Debug.LogException(e); // one bad record must not kill the grid
                return null;
            }
        }

        void UpdateHeader()
        {
            if (headerSub == null) return;
            int total = db != null && db.schools != null ? db.schools.Count : 0;
            int verified = total == 0 ? 0 : db.schools.Count(s => s != null && s.IsVerified);
            headerSub.text = UiText.HeaderSub(total, verified);
        }

        void UpdateSummary(List<School> list)
        {
            if (summaryBar == null) return;
            if (list == null || list.Count == 0)
            {
                summaryBar.text = UiText.SummaryNoMatch;
                return;
            }

            // Summary stats only over schools that have a rate; the rest are counted as "pending".
            List<School> rated = list.Where(s => s.HasRate).ToList();
            if (rated.Count == 0)
            {
                summaryBar.text = UiText.SummaryNoRates(list.Count) + UiText.MissingNote(lastMissing);
                return;
            }
            float avg = rated.Average(s => RateOf(s).Value);
            School best = rated[0];
            foreach (School s in rated)
                if (RateOf(s).Value < RateOf(best).Value) best = s;

            summaryBar.text = UiText.Summary(list.Count, list.Count - rated.Count, avg, best.name, RateOf(best).Value)
                              + UiText.MissingNote(lastMissing);
        }

        void UpdateFilterButtons()
        {
            foreach (KeyValuePair<RateFilter, Button> kv in filterButtons)
                kv.Value.EnableInClassList("active", kv.Key == filter);
        }

        // ------------------------------------------------------------------ selection

        void OnCardClicked(School school)
        {
            if (school == null) return;
            if (selectedId == school.id) ClearSelection();
            else SelectSchool(school);
        }

        void SelectSchool(School school)
        {
            selectedId = school.id;
            SyncCardSelection();
            if (mapView != null) mapView.SetSelected(selectedId);

            if (cardScroll != null)
            {
                if (mapMode && detailPanelEl != null)
                {
                    // Map mode: the panel opens below the map; scroll to it once it has been laid out.
                    VisualElement target = detailPanelEl;
                    cardScroll.schedule.Execute(() => { if (cardScroll != null) cardScroll.ScrollTo(target); }).StartingIn(50);
                }
                else
                {
                    // Phone UX: the detail panel opens above the list; reset the list scroll so the panel/cards start at the top.
                    cardScroll.scrollOffset = Vector2.zero;
                }
            }

            if (detail != null)
            {
                detail.Show(school);
                detail.ShowSection(DetailPanelView.SectionStrategy);
            }
        }

        void ClearSelection()
        {
            selectedId = null;
            SyncCardSelection();
            if (mapView != null) mapView.SetSelected(null);
            if (detail != null) detail.Hide();
            // Map mode: the panel sat below the map and we scrolled down to it; go back up to the map.
            if (mapMode) ScrollToTop();
        }

        // ------------------------------------------------------------------ list / map

        void BindMap()
        {
            mapContainer = uiRoot.Q<VisualElement>("map-view");
            viewListButton = uiRoot.Q<Button>("view-list");
            viewMapButton = uiRoot.Q<Button>("view-map");
            if (viewListButton != null) viewListButton.clicked += () => SetMapMode(false);
            if (viewMapButton != null) viewMapButton.clicked += () => SetMapMode(true);

            if (mapContainer == null)
            {
                Debug.LogWarning("[IvyMatch] 'map-view' not found; map mode is disabled.");
                if (viewMapButton != null) UiUtil.SetVisible(viewMapButton, false);
                return;
            }

            UsMapData data = null;
            try
            {
                TextAsset asset = Resources.Load<TextAsset>(MapResourcePath);
                if (asset != null) data = UsMapData.Parse(asset.text);
                else Debug.LogWarning("[IvyMatch] Resources/" + MapResourcePath + ".json not found.");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[IvyMatch] Map data unusable: " + e.Message);
            }

            mapView = new UsMapView(mapContainer, data);
            mapView.SchoolClicked += OnCardClicked;
        }

        void SetMapMode(bool on)
        {
            if (mapMode == on) return;
            mapMode = on;
            Render();
            ScrollToTop(); // each view starts at its top
        }

        void ScrollToTop()
        {
            if (cardScroll != null) cardScroll.scrollOffset = Vector2.zero;
        }

        /// <summary>Keeps the scroll offset inside [0, content height - viewport height].</summary>
        void ClampScrollOffset()
        {
            if (cardScroll == null) return;
            float contentH = cardScroll.contentContainer.layout.height;
            float viewH = cardScroll.contentViewport.layout.height;
            if (float.IsNaN(contentH) || float.IsNaN(viewH) || viewH <= 0f) return;

            float max = Mathf.Max(0f, contentH - viewH);
            Vector2 o = cardScroll.scrollOffset;
            if (o.y > max + 0.5f || o.y < 0f)
                cardScroll.scrollOffset = new Vector2(o.x, Mathf.Clamp(o.y, 0f, max));
        }

        /// <summary>Shows the map or the card grid and hands the current result list to the map.</summary>
        void UpdateViewMode(List<School> list)
        {
            if (viewListButton != null) viewListButton.EnableInClassList("active", !mapMode);
            if (viewMapButton != null) viewMapButton.EnableInClassList("active", mapMode);
            UiUtil.SetVisible(mapContainer, mapMode);
            UiUtil.SetVisible(cardGrid, !mapMode);
            if (mapView != null && mapMode)
            {
                mapView.SetSchools(list, selectedId);
                // If the map was hidden until now its size is not known yet; redraw once layout has run.
                UsMapView view = mapView;
                mapContainer.schedule.Execute(() => view.Refresh()).StartingIn(30);
            }
        }
    }
}
