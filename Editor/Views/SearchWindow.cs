using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace NewGraph {
    /// <summary>
    /// Based on: https://github.com/Unity-Technologies/UnityCsReference/blob/2022.2/Modules/GraphViewEditor/NodeSearch/SearchWindow.cs
    /// Converted to our own class so this works even if unity decides to change the API.
    /// This is part of the infamous UnityEditor.Experimental.GraphView namespace that is considered deprecated.
    /// However there is no substitution on the horizon for a neat search window like this one.
    /// ///
    /// BE AWARE AS WE ARE USING SOME DARK MAGIC TO CALL AN INTERNAL METHOD HERE.
    /// ///
    /// For documentation on how to use this: Oficially there is none.
    /// Inofficially: I found this video that goes through it in detail: https://www.youtube.com/watch?v=S9NgPKJpJkU
    /// </summary>
    [InitializeOnLoad]
    public class SearchWindow : EditorWindow {

        // Sadly, we need to invoke a method that was marked as "internal" by untiy which prevents us from accessing it.
        // Thankfully, using reflection, we can push through this barrier and still call it. He he he, evil us!
        private static readonly System.Reflection.MethodInfo SearchField = null;
        // binding flags to successfully capture the SearchField method
        private const System.Reflection.BindingFlags methodRetrievalFlags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        // cached version of the searchfield parameters to avoid some garbage as the method is called very often (OnGUI loop)
        private object[] SearchFieldParameters = new object[2];

        /// <summary>
        /// The final EditorGUI.SearchField wrapper
        /// </summary>
        /// <param name="searchRect">Rect of the search field.</param>
        /// <param name="searchString">The string that should be searched for.</param>
        /// <returns>The current search string.</returns>
        private string EditorGUISearchField(Rect searchRect, string searchString) {
            SearchFieldParameters[0] = searchRect;
            SearchFieldParameters[1] = searchString;
            return SearchField.Invoke(null, SearchFieldParameters).ToString();
        }

        // Styles
        class Styles {
            public GUIStyle header = "AC BoldHeader";
            public GUIStyle componentButton = "AC ComponentButton";
            public GUIStyle groupButton = "AC GroupButton";
            public GUIStyle background = "grey_border";
            public GUIStyle rightArrow = "ArrowNavigationRight";
            public GUIStyle leftArrow = "ArrowNavigationLeft";
        }

        // Constants
        private const float k_DefaultWidth = 240f;
        private const float k_DefaultHeight = 270f;
        private const int k_HeaderHeight = 30;
        private const int k_WindowYOffset = 40;
        private const string kSearchHeader = "Search";

        // Static variables
        private static Styles s_Styles;
        public static SearchWindow s_FilterWindow = null;
        private static long s_LastClosedTime;
        private static bool s_DirtyList = false;
        private static Texture2D indentationIcon;
        public static Vector2 targetPosition;
        public static float targetWidth;
        public static float targetHeight;

        // Member variables
        private ScriptableObject m_Owner;

        private ISearchWindowProvider provider { get { return m_Owner as ISearchWindowProvider; } }

        private SearchTreeEntry[] m_Tree;
        private SearchTreeEntry[] m_SearchResultTree;
        private List<SearchTreeGroupEntry> m_SelectionStack = new List<SearchTreeGroupEntry>();

        private float m_Anim = 1;
        private int m_AnimTarget = 1;
        private long m_LastTime = 0;
        private bool m_ScrollToSelected = false;
        private string m_DelayedSearch = null;
        private string m_Search = "";

        // Properties
        private static Texture2D IndentationIcon {
            get {
                if (indentationIcon == null) {
                    indentationIcon = new Texture2D(1, 1);
                    indentationIcon.SetPixel(0, 0, Color.clear);
                    indentationIcon.Apply();
                }
                return indentationIcon;
            }
        }

        private bool hasSearch { get { return !string.IsNullOrEmpty(m_Search); } }
        private SearchTreeGroupEntry activeParent {
            get {
                int index = m_SelectionStack.Count - 2 + m_AnimTarget;

                if (index < 0 || index >= m_SelectionStack.Count)
                    return null;

                return m_SelectionStack[index];
            }
        }

        private SearchTreeEntry[] activeTree { get { return hasSearch ? m_SearchResultTree : m_Tree; } }
        private SearchTreeEntry activeSearchTreeEntry {
            get {
                if (activeTree == null)
                    return null;

                List<SearchTreeEntry> children = GetChildren(activeTree, activeParent);
                if (activeParent == null || activeParent.selectedIndex < 0 || activeParent.selectedIndex >= children.Count)
                    return null;

                return children[activeParent.selectedIndex];
            }
        }
        private bool isAnimating { get { return m_Anim != m_AnimTarget; } }

        // Methods
        static SearchWindow() {
            if (SearchField == null) {
                SearchField = typeof(EditorGUI).GetMethod(nameof(SearchField), methodRetrievalFlags);
            }
            s_DirtyList = true;
        }

        void OnEnable() {
            s_FilterWindow = this;
        }

        void OnDisable() {
            s_LastClosedTime = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;
            s_FilterWindow = null;
        }

        /// <summary>
        /// Actually opens and displays a SearchWindow based on the provided context and the custom class that implements ISearchWindowProvider
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="context">Context like mouse position, window size etc.</param>
        /// <param name="provider">the actual data object/window data content</param>
        /// <returns>Returns if we were able to successfully open the window.</returns>
        public static bool Open<T>(T provider, VisualElement visualElement) where T : ScriptableObject, ISearchWindowProvider {
            // If the window is already open, close it instead.
            UnityEngine.Object[] wins = Resources.FindObjectsOfTypeAll(typeof(SearchWindow));
            if (wins.Length > 0) {
                try {
                    ((EditorWindow)wins[0]).Close();
                    s_FilterWindow = null;
                    return false;
                } catch (Exception) {
                    s_FilterWindow = null;
                }
            }

            // We could not use realtimeSinceStartUp since it is set to 0 when entering/exitting playmode, we assume an increasing time when comparing time.
            long nowMilliSeconds = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;
            bool justClosed = nowMilliSeconds < s_LastClosedTime + 50;
            if (!justClosed) {
                if (s_FilterWindow == null) {
                    s_FilterWindow = CreateInstance<SearchWindow>();
                    s_FilterWindow.hideFlags = HideFlags.HideAndDontSave;
                }
                s_FilterWindow.Init(provider, visualElement); 
                return true;
            }
            return false;
        }

        private Rect GetTargetRect(float width, float height=1) {
            return new Rect(targetPosition.x - width / 2, targetPosition.y - k_WindowYOffset, width, height);
        }

        void Init(ScriptableObject provider, VisualElement visualElement) {

            m_Owner = provider;

            float width = Math.Max(targetWidth, k_DefaultWidth);
            float height = Math.Max(targetHeight, k_DefaultHeight);
            Rect buttonRect = GetTargetRect(width);

            CreateSearchTree();

            ShowAsDropDown(buttonRect, new Vector2(buttonRect.width, height));
            position = buttonRect;
            
            visualElement.schedule.Execute(() => {
                Rect rect = GetTargetRect(width, height);
                position = rect;
            });
            
            Focus();

            wantsMouseMove = true;
        }

        private void CreateSearchTree() {
            List<SearchTreeEntry> tree = provider.CreateSearchTree();

            foreach (SearchTreeEntry treeEntry in tree) {
                if (!(treeEntry is SearchTreeGroupEntry) && !(treeEntry is InlineHeaderEntry) && treeEntry.level >= 1 && treeEntry.content.image == null) {
                    treeEntry.content.image = IndentationIcon;
                }
            }

            if (tree != null)
                m_Tree = tree.ToArray();
            else
                m_Tree = new SearchTreeEntry[0];

            // Rebuild stack
            if (m_SelectionStack.Count == 0)
                m_SelectionStack.Add(m_Tree[0] as SearchTreeGroupEntry);
            else {
                // The root is always the match for level 0
                SearchTreeGroupEntry match = m_Tree[0] as SearchTreeGroupEntry;
                int level = 0;
                while (true) {
                    // Assign the match for the current level
                    SearchTreeGroupEntry oldSearchTreeEntry = m_SelectionStack[level];
                    m_SelectionStack[level] = match;
                    m_SelectionStack[level].selectedIndex = oldSearchTreeEntry.selectedIndex;
                    m_SelectionStack[level].scroll = oldSearchTreeEntry.scroll;

                    // See if we reached last SearchTreeEntry of stack
                    level++;
                    if (level == m_SelectionStack.Count)
                        break;

                    // Try to find a child of the same name as we had before
                    List<SearchTreeEntry> children = GetChildren(activeTree, match);
                    SearchTreeEntry childMatch = children.FirstOrDefault(c => c.name == m_SelectionStack[level].name);
                    if (childMatch != null && childMatch is SearchTreeGroupEntry) {
                        match = childMatch as SearchTreeGroupEntry;
                    } else {
                        // If we couldn't find the child, remove all further SearchTreeEntrys from the stack
                        m_SelectionStack.RemoveRange(level, m_SelectionStack.Count - level);
                    }
                }
            }

            s_DirtyList = false;
            RebuildSearch();
        }

        /*
        private void CreateGUI() {
            rootVisualElement.Add(new Label() { text = "jjjj" });
        }
        */
        
        //Vector2 scroll = Vector2.zero;
        internal void OnGUI() {
            if (s_Styles == null)
                s_Styles = new Styles();

            GUI.Label(new Rect(0, 0, position.width, position.height), GUIContent.none, s_Styles.background);
            
            if (s_DirtyList)
                CreateSearchTree();

            // Keyboard
            HandleKeyboard();

            GUILayout.Space(7);

            // Search
            EditorGUI.FocusTextInControl("ComponentSearch");

            Rect searchRect = GUILayoutUtility.GetRect(10, 20);
            searchRect.x += 8;
            searchRect.width -= 16;

            GUI.SetNextControlName("ComponentSearch");

            EditorGUI.BeginChangeCheck();

            string newSearch = EditorGUISearchField(searchRect, m_DelayedSearch ?? m_Search);

            if (EditorGUI.EndChangeCheck() && (newSearch != m_Search || m_DelayedSearch != null)) {
                if (!isAnimating) {
                    m_Search = m_DelayedSearch ?? newSearch;
                    RebuildSearch();
                    m_DelayedSearch = null;
                } else {
                    m_DelayedSearch = newSearch;
                }
            }
            //scroll = EditorGUILayout.BeginScrollView(scroll, false, true, GUILayout.ExpandHeight(true));
            // Show lists
            ListGUI(activeTree, m_Anim, GetSearchTreeEntryRelative(0), GetSearchTreeEntryRelative(-1));
            if (m_Anim < 1)
                ListGUI(activeTree, m_Anim + 1, GetSearchTreeEntryRelative(-1), GetSearchTreeEntryRelative(-2));

            // Animate
            if (isAnimating && Event.current.type == EventType.Repaint) {
                long now = System.DateTime.Now.Ticks;
                float deltaTime = (now - m_LastTime) / (float)System.TimeSpan.TicksPerSecond;
                m_LastTime = now;
                m_Anim = Mathf.MoveTowards(m_Anim, m_AnimTarget, deltaTime * 4);
                if (m_AnimTarget == 0 && m_Anim == 0) {
                    m_Anim = 1;
                    m_AnimTarget = 1;
                    m_SelectionStack.RemoveAt(m_SelectionStack.Count - 1);
                }
                Repaint();
            }
            //EditorGUILayout.EndScrollView();
        }
        
        private void HandleKeyboard() {
            Event evt = Event.current;
            if (evt.type == EventType.KeyDown) {
                // Always do these
                if (evt.keyCode == KeyCode.DownArrow) {
                    activeParent.selectedIndex++;
                    activeParent.selectedIndex = Mathf.Min(activeParent.selectedIndex, GetChildren(activeTree, activeParent).Count - 1);
                    m_ScrollToSelected = true;
                    evt.Use();
                }
                if (evt.keyCode == KeyCode.UpArrow) {
                    activeParent.selectedIndex--;
                    activeParent.selectedIndex = Mathf.Max(activeParent.selectedIndex, 0);
                    m_ScrollToSelected = true;
                    evt.Use();
                }
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter) {
                    if (activeSearchTreeEntry != null) {
                        SelectEntry(activeSearchTreeEntry, true);
                        evt.Use();
                    }
                }

                // Do these if we're not in search mode
                if (!hasSearch) {
                    if (evt.keyCode == KeyCode.LeftArrow || evt.keyCode == KeyCode.Backspace) {
                        GoToParent();
                        evt.Use();
                    }
                    if (evt.keyCode == KeyCode.RightArrow) {
                        if (activeSearchTreeEntry != null) {
                            SelectEntry(activeSearchTreeEntry, false);
                            evt.Use();
                        }
                    }
                    if (evt.keyCode == KeyCode.Escape) {
                        Close();
                        evt.Use();
                    }
                }
            }
        }

        private void RebuildSearch() {
            if (!hasSearch) {
                m_SearchResultTree = null;
                if (m_SelectionStack[m_SelectionStack.Count - 1].name == kSearchHeader) {
                    m_SelectionStack.Clear();
                    m_SelectionStack.Add(m_Tree[0] as SearchTreeGroupEntry);
                }
                m_AnimTarget = 1;
                m_LastTime = System.DateTime.Now.Ticks;
                return;
            }

            // Support multiple search words separated by spaces.
            string[] searchWords = m_Search.ToLower().Split(' ');

            // We keep two lists. Matches that matches the start of an item always get first priority.
            List<SearchTreeEntry> matchesStart = new List<SearchTreeEntry>();
            List<SearchTreeEntry> matchesWithin = new List<SearchTreeEntry>();

            foreach (SearchTreeEntry e in m_Tree) {
                if ((e is SearchTreeGroupEntry) || (e is InlineHeaderEntry))
                    continue;

                string name = e.name.ToLower().Replace(" ", "");
                bool didMatchAll = true;
                bool didMatchStart = false;

                // See if we match ALL the seaarch words.
                for (int w = 0; w < searchWords.Length; w++) {
                    string search = searchWords[w];
                    if (name.Contains(search)) {
                        // If the start of the item matches the first search word, make a note of that.
                        if (w == 0 && name.StartsWith(search))
                            didMatchStart = true;
                    } else {
                        // As soon as any word is not matched, we disregard this item.
                        didMatchAll = false;
                        break;
                    }
                }
                // We always need to match all search words.
                // If we ALSO matched the start, this item gets priority.
                if (didMatchAll) {
                    if (didMatchStart)
                        matchesStart.Add(e);
                    else
                        matchesWithin.Add(e);
                }
            }

            matchesStart.Sort();
            matchesWithin.Sort();

            // Create search tree
            List<SearchTreeEntry> tree = new List<SearchTreeEntry>();
            // Add parent
            tree.Add(new SearchTreeGroupEntry(kSearchHeader, SearchTreeEntry.AlwaysEnabled));
            // Add search results
            tree.AddRange(matchesStart);
            tree.AddRange(matchesWithin);

            // Create search result tree
            m_SearchResultTree = tree.ToArray();
            m_SelectionStack.Clear();
            m_SelectionStack.Add(m_SearchResultTree[0] as SearchTreeGroupEntry);

            // Always select the first search result when search is changed (e.g. a character was typed in or deleted),
            // because it's usually the best match.
            if (GetChildren(activeTree, activeParent).Count >= 1) {
                activeParent.selectedIndex = -1;
                List<SearchTreeEntry> entries = GetChildren(activeTree, activeParent);
                for (int i = 0; i < entries.Count; i++) {
                    if (entries[i].enabledCheck()) {
                        activeParent.selectedIndex = i;
                        break;
                    }
                }
            } else {
                activeParent.selectedIndex = -1;
            }
        }

        private SearchTreeGroupEntry GetSearchTreeEntryRelative(int rel) {
            int i = m_SelectionStack.Count + rel - 1;
            if (i < 0 || i >= m_SelectionStack.Count)
                return null;
            return m_SelectionStack[i] as SearchTreeGroupEntry;
        }

        private void GoToParent() {
            if (m_SelectionStack.Count > 1) {
                m_AnimTarget = 0;
                m_LastTime = System.DateTime.Now.Ticks;
            }
        }

        private void ListGUI(SearchTreeEntry[] tree, float anim, SearchTreeGroupEntry parent, SearchTreeGroupEntry grandParent) {
            // Smooth the fractional part of the anim value
            anim = Mathf.Floor(anim) + Mathf.SmoothStep(0, 1, Mathf.Repeat(anim, 1));

            // Calculate rect for animated area
            Rect animRect = position;
            animRect.x = position.width * (1 - anim) + 1;
            animRect.y = k_HeaderHeight;
            //animRect.height -= k_HeaderHeight;
            animRect.width -= 2;

            // Start of animated area (the part that moves left and right)
            GUILayout.BeginArea(animRect);

            // Header
            Rect headerRect = GUILayoutUtility.GetRect(10, 25);
            string name = parent.name;
            GUI.Label(headerRect, name, s_Styles.header);

            // Back button
            if (grandParent != null) {
                float yOffset = (headerRect.height - s_Styles.leftArrow.fixedHeight) / 2;
                Rect arrowRect = new Rect(
                    headerRect.x + s_Styles.leftArrow.margin.left,
                    headerRect.y + yOffset,
                    s_Styles.leftArrow.fixedWidth,
                    s_Styles.leftArrow.fixedHeight);
                if (Event.current.type == EventType.Repaint)
                    s_Styles.leftArrow.Draw(arrowRect, false, false, false, false);
                if (Event.current.type == EventType.MouseDown && headerRect.Contains(Event.current.mousePosition)) {
                    GoToParent();
                    Event.current.Use();
                }
            }

            ListGUI(tree, parent);

            GUILayout.EndArea();
        }

        private void SelectEntry(SearchTreeEntry e, bool shouldInvokeCallback) {

            if (e is SearchTreeGroupEntry) {
                if (!hasSearch) {
                    m_LastTime = System.DateTime.Now.Ticks;
                    if (m_AnimTarget == 0)
                        m_AnimTarget = 1;
                    else if (m_Anim == 1) {
                        m_Anim = 0;
                        m_SelectionStack.Add(e as SearchTreeGroupEntry);
                    }
                }
            } else if (shouldInvokeCallback) {
                e.actionToExecute?.Invoke(null);
                if (e.actionToExecute != null) {
                    Close();
                }
            }
        }

        private void ListGUI(SearchTreeEntry[] tree, SearchTreeGroupEntry parent) {
            // Start of scroll view list
            parent.scroll = GUILayout.BeginScrollView(parent.scroll);

            EditorGUIUtility.SetIconSize(new Vector2(16, 16));

            List<SearchTreeEntry> children = GetChildren(tree, parent);

            Rect selectedRect = new Rect();

            // Iterate through the children
            for (int i = 0; i < children.Count; i++) {
                bool guiWasEnabled = GUI.enabled;
                SearchTreeEntry e = children[i];
                GUI.enabled = e.enabledCheck();
                Rect r = GUILayoutUtility.GetRect(16, (e is InlineHeaderEntry) ? 25 : 20, GUILayout.ExpandWidth(true));

                // Select the SearchTreeEntry the mouse cursor is over.
                // Only do it on mouse move - keyboard controls are allowed to overwrite this until the next time the mouse moves.
                if (Event.current.type == EventType.MouseMove || Event.current.type == EventType.MouseDown) {
                    if (parent.selectedIndex != i && r.Contains(Event.current.mousePosition)) {
                        parent.selectedIndex = i;
                        Repaint();
                    }
                }

                bool selected = false;
                // Handle selected item
                if (i == parent.selectedIndex) {
                    selected = true;
                    selectedRect = r;
                }

                // Draw SearchTreeEntry
                if (Event.current.type == EventType.Repaint) {
                    GUIStyle labelStyle = (e is SearchTreeGroupEntry) ? s_Styles.groupButton : s_Styles.componentButton;
                    //GUIContent newC = new GUIContent(e.content.text + "test") { image = e.content.image };

                    if (e is InlineHeaderEntry) {
                        GUI.Label(r, e.content, s_Styles.header);
                    } else {
                        labelStyle.Draw(r, e.content, false, false, selected, selected);
                        if (e is SearchTreeGroupEntry) {
                            float yOffset = (r.height - s_Styles.rightArrow.fixedHeight) / 2;
                            Rect arrowRect = new Rect(
                                r.xMax - s_Styles.rightArrow.fixedWidth - s_Styles.rightArrow.margin.right,
                                r.y + yOffset,
                                s_Styles.rightArrow.fixedWidth,
                                s_Styles.rightArrow.fixedHeight);
                            s_Styles.rightArrow.Draw(arrowRect, false, false, false, false);
                        }
                    }
                }
                if (!(e is InlineHeaderEntry) && Event.current.type == EventType.MouseDown && r.Contains(Event.current.mousePosition)) {
                    Event.current.Use();
                    parent.selectedIndex = i;
                    SelectEntry(e, true);
                }
                GUI.enabled = guiWasEnabled;
            }

            EditorGUIUtility.SetIconSize(Vector2.zero);

            GUILayout.EndScrollView();

            // Scroll to show selected
            if (m_ScrollToSelected && Event.current.type == EventType.Repaint) {
                m_ScrollToSelected = false;
                Rect scrollRect = GUILayoutUtility.GetLastRect();
                if (selectedRect.yMax - scrollRect.height > parent.scroll.y) {
                    parent.scroll.y = selectedRect.yMax - scrollRect.height;
                    Repaint();
                }
                if (selectedRect.y < parent.scroll.y) {
                    parent.scroll.y = selectedRect.y;
                    Repaint();
                }
            }
        }

        private List<SearchTreeEntry> GetChildren(SearchTreeEntry[] tree, SearchTreeEntry parent) {
            List<SearchTreeEntry> children = new List<SearchTreeEntry>();
            int level = -1;
            int i = 0;
            for (i = 0; i < tree.Length; i++) {
                if (tree[i] == parent) {
                    level = parent.level + 1;
                    i++;
                    break;
                }
            }
            if (level == -1)
                return children;

            for (; i < tree.Length; i++) {
                SearchTreeEntry e = tree[i];

                if (e.level < level)
                    break;
                if (e.level > level && !hasSearch)
                    continue;

                children.Add(e);
            }

            return children;
        }
    }
}
