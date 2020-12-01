using System;
using System.Collections.Generic;
using JetBrains.Annotations;
using NUnit.Framework;
using Unity.Properties.Internal;
using Unity.Properties.UI.Internal;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Unity.Properties.UI
{
    /// <summary>
    /// Interface used to receive search query callbacks.
    /// </summary>
    /// <typeparam name="TData">The search data type.</typeparam>
    public interface ISearchQueryHandler<TData>
    {
        /// <summary>
        /// This method is invoked whenever a search is performed. 
        /// </summary>
        /// <param name="query">The query which can be used to apply the search to some data.</param>
        void HandleSearchQuery(ISearchQuery<TData> query);
    }

    /// <summary>
    /// Interface used to apply a search query to a given set of data.
    /// </summary>
    /// <typeparam name="TData">The search data type.</typeparam>
    public interface ISearchQuery<TData>
    {
        /// <summary>
        /// Gets the original search string for the query.
        /// </summary>
        string SearchString { get; }
        
        /// <summary>
        /// List of tokens found in the query.
        /// </summary>
        ICollection<string> Tokens { get; }
        
        /// <summary>
        /// Applies the search filters to the specified <see cref="IEnumerable{T}"/> data set.
        /// </summary>
        /// <param name="data">The data set to filter.</param>
        /// <returns>An <see cref="IEnumerable{T}"/> which returns the filtered data set.</returns>
        IEnumerable<TData> Apply(IEnumerable<TData> data);
    }
    
    /// <summary>
    /// Represents a reusable control for searching and filtering.
    /// </summary>
    [UsedImplicitly]
    public sealed class SearchElement : VisualElement, INotifyValueChanged<string>
    {
        /// <summary>
        /// Instantiates a SearchElement using the data read from a UXML file.
        /// </summary>
        [UsedImplicitly]
        class SearchElementFactory : UxmlFactory<SearchElement, SearchElementTraits> { }

        /// <summary>
        /// Defines UxmlTraits for the SearchElement.
        /// </summary>
        [UsedImplicitly]
        class SearchElementTraits : UxmlTraits
        {
            readonly UxmlStringAttributeDescription m_SearchData = new UxmlStringAttributeDescription {name = "search-data", defaultValue = string.Empty};
            readonly UxmlStringAttributeDescription m_SearchFilters = new UxmlStringAttributeDescription {name = "search-filters", defaultValue = string.Empty};
            readonly UxmlStringAttributeDescription m_SourceData = new UxmlStringAttributeDescription {name = "source-data", defaultValue = string.Empty};
            readonly UxmlStringAttributeDescription m_FilteredData = new UxmlStringAttributeDescription {name = "filtered-data", defaultValue = string.Empty};
            readonly UxmlStringAttributeDescription m_HandlerType = new UxmlStringAttributeDescription {name = "handler-type", defaultValue = "sync"};
            readonly UxmlIntAttributeDescription m_SearchDelay = new UxmlIntAttributeDescription {name = "search-delay", defaultValue = 200};
            readonly UxmlIntAttributeDescription m_MaxFrameTime = new UxmlIntAttributeDescription {name = "max-frame-time", defaultValue = 33};

            public override void Init(VisualElement element, IUxmlAttributes attributes, CreationContext context)
            {
                base.Init(element, attributes, context);
                
                var search = (SearchElement) element;
                
                search.m_SearchEngine.Clear();
                search.m_FilterPopupElementItems.Clear();
                
                foreach (var value in m_SearchData.GetValueFromBag(attributes, context).Split(' '))
                {
                    if (string.IsNullOrEmpty(value))
                        continue;
                        
                    search.AddSearchDataProperty(new PropertyPath(value));
                }
                
                foreach (var value in m_SearchFilters.GetValueFromBag(attributes, context).Split(' '))
                {
                    var filter = value.Split(':');

                    if (filter.Length != 2 || string.IsNullOrEmpty(filter[0]) || string.IsNullOrEmpty(filter[1])) 
                        continue;

                    var token = filter[0];
                    var path = new PropertyPath(filter[1]);

                    try
                    {
                        search.AddSearchFilterProperty(token, path);
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning(e.Message);
                    }
                    
                    search.AddSearchFilterPopupItem(token, path.ToString().SplitPascalCase());
                }

                var sourceData = m_SourceData.GetValueFromBag(attributes, context);
                var filteredData = m_FilteredData.GetValueFromBag(attributes, context);

                if (!Enum.TryParse(m_HandlerType.GetValueFromBag(attributes, context), out SearchHandlerType handlerType))
                {
                    Debug.LogWarning($"SearchElement has invalid HandlerType=[{m_HandlerType.GetValueFromBag(attributes, context)}]. Expected values are [{string.Join(",", Enum.GetNames(typeof(SearchHandlerType)))}]. Defaulting to {nameof(SearchHandlerType.sync)}");
                    handlerType = SearchHandlerType.sync;
                }

                var maxFrameTime = m_MaxFrameTime.GetValueFromBag(attributes, context);

                if (!string.IsNullOrEmpty(sourceData) && !string.IsNullOrEmpty(filteredData))
                {
                    if (sourceData != filteredData)
                    {
                        search.m_UxmlSearchHandlerBinding = new SearchHandlerBinding
                        {
                            SourceDataPath = new PropertyPath(sourceData),
                            FilteredDataPath = new PropertyPath(filteredData),
                            HandlerType = handlerType,
                            MaxFrameTime = maxFrameTime,
                            SearchHandler = null,
                        }; 
                    }
                    else
                    {
                        Debug.LogWarning($"SearchElement has invalid data bindings. SourceData=[{sourceData}] FilteredData=[{filteredData}]. Can not read and write to the same property.");
                    }
                }
                else if (!string.IsNullOrEmpty(sourceData))
                {
                    Debug.LogWarning("SearchElement has invalid data bindings. The 'source-data' attribute requires the 'filtered-data' to also be set.");
                }
                else if (!string.IsNullOrEmpty(filteredData))
                {
                    Debug.LogWarning("SearchElement has invalid data bindings. The 'filtered-data' attribute requires the 'source-data' to also be set.");
                }

                search.SearchDelay = m_SearchDelay.GetValueFromBag(attributes, context);
            }
        }
        
        /// <summary>
        /// Helper class to store data related to uxml bindings for deferred execution.
        /// </summary>
        class SearchHandlerBinding
        {
            /// <summary>
            /// The <see cref="PropertyPath"/> which holds to source data to be searched.
            /// </summary>
            public PropertyPath SourceDataPath;
            
            /// <summary>
            /// The <see cref="PropertyPath"/> which the filtered results should be placed.
            /// </summary>
            public PropertyPath FilteredDataPath;

            /// <summary>
            /// The <see cref="SearchHandlerType"/> type to use.
            /// </summary>
            public SearchHandlerType HandlerType;

            /// <summary>
            /// The maximum number of milliseconds spent on processing the search per frame.
            /// </summary>
            public int MaxFrameTime;
            
            /// <summary>
            /// The reference to the strongly typed search query handler.
            /// </summary>
            public ISearchHandler SearchHandler;
        }
        
        /// <summary>
        /// Visitor class used to register source data bindings from property paths. i.e. The data which the search should be performed on.
        /// </summary>
        class SourceDataBindingVisitor : PropertyVisitor
        {
            public SearchElement SearchElement;
            public PropertyElement PropertyElement;
            public PropertyPath SourceDataPath;
            public ISearchHandler SearchHandler;

            protected override void VisitProperty<TContainer, TValue>(Property<TContainer, TValue> property, ref TContainer container, ref TValue value)
            {
                throw new InvalidBindingException($"SearchElement has invalid data bindings. SourceDataPath=[{SourceDataPath}] SourceDataType=[{typeof(TValue)}] is not a collection type");
            }

            protected override void VisitCollection<TContainer, TCollection, TElement>(Property<TContainer, TCollection> property, ref TContainer container, ref TCollection value)
            {
                if (RuntimeTypeInfoCache<TCollection>.CanBeNull && null == value)
                    throw new InvalidBindingException($"SearchElement has invalid data bindings. SourceDataPath=[{SourceDataPath}] is null.");

                var handler = new SearchHandler<TElement>(SearchElement);

                var root = PropertyElement;
                var path = SourceDataPath;
                
                handler.SetSearchDataProvider(() =>
                {
                    var filtered = root.GetValue<TCollection>(path);
                    
                    if (RuntimeTypeInfoCache<TCollection>.CanBeNull && null == filtered)
                        throw new InvalidBindingException($"SearchElement has invalid data bindings. SourceDataPath=[{path}] is null.");
                    
                    return filtered;
                });

                SearchHandler = handler;
            }
        }

        /// <summary>
        /// Visitor class used to register destination data bindings from property paths. i.e. The data which the filtered results should be written to.
        /// </summary>
        class FilterDataBindingVisitor : PropertyVisitor
        {
            public PropertyElement PropertyElement;
            public PropertyPath SourceDataPath;
            public PropertyPath FilterDataPath;
            public ISearchHandler SearchHandler;

            protected override void VisitProperty<TContainer, TValue>(Property<TContainer, TValue> property, ref TContainer container, ref TValue value)
            {
                throw new InvalidBindingException($"SearchElement has invalid data bindings. FilterDataPath=[{FilterDataPath}] FilterDataType=[{typeof(TValue)}] is not a collection type");
            }

            protected override void VisitCollection<TContainer, TCollection, TElement>(Property<TContainer, TCollection> property, ref TContainer container, ref TCollection value)
            {
                UnityEngine.Assertions.Assert.IsNotNull(SearchHandler, $"{nameof(SourceDataBindingVisitor)} failed to construct the {nameof(SearchHandler<TElement>)}");

                if (RuntimeTypeInfoCache<TCollection>.CanBeNull && null == value)
                    throw new InvalidBindingException($"SearchElement has invalid data bindings. FilterDataPath=[{FilterDataPath}] is null.");

                if (!(SearchHandler is SearchHandler<TElement> typed))
                    throw new InvalidBindingException($"SearchElement has invalid data bindings. SourceDataPath=[{SourceDataPath}] SourceDataType=[{SearchHandler.SearchDataType}] and FilterDataPath=[{FilterDataPath}] FilterDataType=[{typeof(TElement)}] types do not match.");

                // @NOTE We can possibly add some magic here to handle `readonly` collection types (i.e. Array) by re-creating an instance and assigning each search.
                //       but for now we will just error out.
                if (value.IsReadOnly)
                    throw new InvalidBindingException($"SearchElement has invalid data bindings. FilterDataPath=[{FilterDataPath}] is ReadOnly.");
                
                var root = PropertyElement;
                var path = FilterDataPath;
                
                typed.OnBeginSearch += _ =>
                {
                    var filtered = root.GetValue<TCollection>(path);

                    if (RuntimeTypeInfoCache<TCollection>.CanBeNull && null == filtered)
                        throw new InvalidBindingException($"SearchElement has invalid data bindings. FilterDataPath=[{path}] is null.");

                    filtered.Clear();
                };
                
                typed.OnFilter += (_, elements) =>
                {
                    var filtered = root.GetValue<TCollection>(path);
                    
                    if (RuntimeTypeInfoCache<TCollection>.CanBeNull && null == filtered)
                        throw new InvalidBindingException($"SearchElement has invalid data bindings. FilterDataPath=[{path}] is null.");
                    
                    foreach (var element in elements)
                        filtered.Add(element);
                };
            }
        }
        
        struct FilterPopupElementChoice
        {
            public string Token;
            public string Text;
            public string Tooltip;
        }

        /// <summary>
        /// The add filter dropdown used to show which filters are available and quickly add them to the search string.
        /// </summary>
        class FilterPopupElement : PopupElement
        {
            readonly float m_Width;
            readonly SearchElement m_SearchElement;
            readonly VisualElement m_Choices;
            
            protected override Vector2 GetSize() => new Vector2(m_Width, worldBound.height);

            /// <summary>
            /// Constructs a new instance of the <see cref="SearchElement"/> control.
            /// </summary>
            public FilterPopupElement(SearchElement searchElement, int width)
            {
                m_SearchElement = searchElement;
                m_Width = width;
                
                // Setup the root element and content.
                Internal.Resources.Templates.Common.AddStyles(this);
                Internal.Resources.Templates.SearchElementFilterPopup.Clone(this);
                AddToClassList(UssClasses.SearchElementFilterPopup.Root);
                
                m_Choices = this.Q<VisualElement>("search-element-filter-popup-choices");

                Assert.IsNotNull(m_Choices);
            }

            // ReSharper disable once ParameterHidesMember
            public void AddPopupItem(string token, string filterText, string filterTooltip = "")
            {
                // Create a button with two labels.
                var choiceButton = new Button { tooltip = filterTooltip };
                var nameLabel = new Label(filterText);
                var tokenLabel = new Label(token + ":");

                // Setup uss classes.
                choiceButton.ClearClassList();
                choiceButton.AddToClassList(UssClasses.SearchElementFilterPopup.ChoiceButton);
                nameLabel.AddToClassList(UssClasses.SearchElementFilterPopup.ChoiceName);
                tokenLabel.AddToClassList(UssClasses.SearchElementFilterPopup.ChoiceToken);
                
                // Setup visual tree.
                choiceButton.Add(nameLabel);
                choiceButton.Add(tokenLabel);
                m_Choices.Add(choiceButton);
                
                // Setup event handlers.
                choiceButton.clickable.clicked += () =>
                {
                    // Since this is an incomplete filter no need to trigger a search.
                    m_SearchElement.SetValueWithoutNotify(token + ":");
                    
                    // However we do need to manually update the controls and re-focus.
                    m_SearchElement.UpdateControls();
                    m_SearchElement.FocusSearchString();
                    
                    // Close the window.
                    Close();
                };
            }
        }

        /// <summary>
        /// This interface is used to abstract the strongly typed search engine methods and callbacks.
        /// </summary>
        interface ISearchTarget
        {
            ISearchHandler GetSearchHandler();
            void Parse(string text);
        } 
        
        /// <summary>
        /// This class is used to store a reference to a strongly typed query handler callback.
        /// </summary>
        /// <typeparam name="TData">The search data type.</typeparam>
        class SearchTarget<TData> : ISearchTarget
        {
            readonly SearchElement SearchElement;
            
            public readonly ISearchQueryHandler<TData> SearchQueryHandler;
            public readonly Action<ISearchQuery<TData>> SearchQueryCallback;
            
            public SearchTarget(SearchElement searchElement, ISearchQueryHandler<TData> searchQueryHandler)
            {
                SearchElement = searchElement;
                SearchQueryHandler = searchQueryHandler;
                SearchQueryCallback = searchQueryHandler.HandleSearchQuery;
            }

            public SearchTarget(SearchElement searchElement, Action<ISearchQuery<TData>> searchQueryCallback)
            {
                SearchElement = searchElement;
                SearchQueryHandler = null;
                SearchQueryCallback = searchQueryCallback;
            }

            public ISearchHandler GetSearchHandler()
            {
                return SearchQueryHandler as ISearchHandler;
            }

            public void Parse(string text)
            {
                SearchQueryCallback(SearchElement.m_SearchEngine.Parse<TData>(text)); 
            }
        }

        /// <summary>
        /// The search handler bindings registered from uxml traits.
        /// </summary>
        SearchHandlerBinding m_UxmlSearchHandlerBinding;
        
        /// <summary>
        /// The engine used to perform the actual searching.
        /// </summary>
        readonly SearchEngine m_SearchEngine = new SearchEngine();

        /// <summary>
        /// The container which holds the graph fields.
        /// </summary>
        readonly TextField m_SearchStringTextField;

        /// <summary>
        /// The add filter button.
        /// </summary>
        readonly Button m_AddFilterButton;
        
        /// <summary>
        /// The cancel filter button.
        /// </summary>
        readonly Button m_CancelButton;

        /// <summary>
        /// The progress bar used to draw the search progress.
        /// </summary>
        readonly ProgressBar m_ProgressBar;
        
        /// <summary>
        /// Collection of strongly typed search query handlers.
        /// </summary>
        readonly List<ISearchTarget> m_SearchTargets = new List<ISearchTarget>();

        /// <summary>
        /// The handle to the current delayed search.
        /// </summary>
        IVisualElementScheduledItem m_DelayedSearch;

        /// <summary>
        /// The list of items to show for the filter dropdown.
        /// </summary>
        readonly List<FilterPopupElementChoice> m_FilterPopupElementItems = new List<FilterPopupElementChoice>();
        
        /// <summary>
        /// The desired backend type to use. This defaults to <see cref="SearchBackendType.QuickSearch"/> and should only be changed for tests.
        /// </summary>
        internal SearchBackendType BackendType
        {
            get => m_SearchEngine.BackendType;
            set => m_SearchEngine.BackendType = value;
        }
        
        /// <summary>
        /// Gets or sets the search string value. This is the string that appears in the text box.
        /// </summary>
        /// <remarks>
        /// Setting this value will trigger the search. To update the value without searching use <seealso cref="SetValueWithoutNotify"/>.
        /// </remarks>
        public string value
        {
            get => m_SearchStringTextField.value;
            set => m_SearchStringTextField.value = value;
        }

        /// <summary>
        /// Gets or sets the search delay. This is the number of millisecond after input is receive for the search to be executed. The default value is 200.
        /// </summary>
        public long SearchDelay { get; set; } = 200;

        /// <summary>
        /// Gets or sets the desired width for the popup element. The default value is 175.
        /// </summary>
        public int FilterPopupWidth { get; set; } = 175;

        /// <summary>
        /// Constructs a new instance of the <see cref="SearchElement"/> control.
        /// </summary>
        public SearchElement()
        { 
            // Setup the root element and content.
            Internal.Resources.Templates.Common.AddStyles(this);
            Internal.Resources.Templates.SearchElement.Clone(this);
            
            AddToClassList(UssClasses.SearchElement.Root);
            AddToClassList("unity-base-field");
            AddToClassList("unity-properties__variables");
            
            name = "search-element";
            
            m_SearchStringTextField = this.Q("search-element-text-field-search-string") as TextField;
            m_AddFilterButton = this.Q("search-element-add-filter-button") as Button;
            m_CancelButton = this.Q("search-element-cancel-button") as Button;
            m_ProgressBar = this.Q("search-element-progress-bar") as ProgressBar;
            
            UnityEngine.Assertions.Assert.IsNotNull(m_SearchStringTextField);
            UnityEngine.Assertions.Assert.IsNotNull(m_AddFilterButton);
            UnityEngine.Assertions.Assert.IsNotNull(m_CancelButton);
            UnityEngine.Assertions.Assert.IsNotNull(m_ProgressBar);

            m_CancelButton.clickable.clicked += ClearSearchString;
            
            m_SearchStringTextField.RegisterValueChangedCallback(evt =>
            {
                SearchDelayed(SearchDelay);
                UpdateControls();
            });
            
            m_SearchStringTextField.RegisterCallback<KeyUpEvent, SearchElement>((evt, element) =>
            {
                if (evt.keyCode == KeyCode.Escape) element.Search();
            }, this);
            
            m_AddFilterButton.clickable.clicked += () =>
            {
                if (m_FilterPopupElementItems.Count == 0) return;
                
                var filterDropdown = new FilterPopupElement(this, FilterPopupWidth);

                foreach (var item in m_FilterPopupElementItems)
                    filterDropdown.AddPopupItem(item.Token, item.Text, item.Tooltip);

                filterDropdown.ShowAtPosition(m_AddFilterButton.worldBound);
            };

            HideProgress();
            UpdateControls();
        }

        /// <summary>
        /// This method is invoked by the custom inspector just after the visual tree is built. This method can be used to resolve property bindings.
        /// </summary>
        /// <param name="propertyElement">The root visual element hosting the data.</param>
        internal void ResolveSearchHandlerBindings(PropertyElement propertyElement)
        {
            if (null == m_UxmlSearchHandlerBinding)
                return;

            if (null != m_UxmlSearchHandlerBinding.SearchHandler)
            {
                UnregisterSearchQueryHandler(m_UxmlSearchHandlerBinding.SearchHandler);
                m_UxmlSearchHandlerBinding.SearchHandler = null;
            }

            var handler = default(ISearchHandler);
            
            try
            {
                handler = RegisterSearchQueryHandler(propertyElement, m_UxmlSearchHandlerBinding.SourceDataPath, m_UxmlSearchHandlerBinding.FilteredDataPath);
            }
            catch (InvalidBindingException e)
            {
                // Since this is registered from and asset and invoked during property element construction we don't want to throw.
                // Instead we just let the user know there was a bindings issue and exit out.
                Debug.LogWarning(e.Message);
                return;
            }

            if (null == handler)
                return;

            handler.Mode = m_UxmlSearchHandlerBinding.HandlerType;
            handler.MaxFrameProcessingTimeMs = m_UxmlSearchHandlerBinding.MaxFrameTime;
            m_UxmlSearchHandlerBinding.SearchHandler = handler;
            Search();
        }

        /// <summary>
        /// Returns the search handler registered through UXML bindings.
        /// </summary>
        /// <returns>The search handler created from UXML bindings.</returns>
        public ISearchHandler GetUxmlSearchHandler() => m_UxmlSearchHandlerBinding?.SearchHandler;
        
        /// <summary>
        /// Updates the search string value without invoking the search.
        /// </summary>
        /// <param name="newValue">The search string value to set.</param>
        public void SetValueWithoutNotify(string newValue)
        {
            m_SearchStringTextField.SetValueWithoutNotify(newValue);
        }
        
        /// <summary>
        /// Shows and updates the progress bar for the search field.
        /// </summary>
        /// <param name="progress">The progress value to show. Range should be 0 to 1.</param>
        public void ShowProgress(float progress)
        {
            // We need to multiply here since the highValue is internal and defaults to 100. 
            m_ProgressBar.value = progress * 100f;
            m_ProgressBar.style.visibility = Visibility.Visible;
        }
        
        /// <summary>
        /// Hides the progress bar for the search field.
        /// </summary>
        public void HideProgress()
        {
            m_ProgressBar.style.visibility = Visibility.Hidden;
        }

        /// <summary>
        /// Adds a property who's value should be compared against search string.
        /// </summary>
        /// <param name="path">The property path to pull search data from.</param>
        public void AddSearchDataProperty(PropertyPath path)
        {
            m_SearchEngine.AddSearchDataProperty(path);
        }

        /// <summary>
        /// Adds a callback which returns values that should be compared against the search string.
        /// </summary>
        /// <param name="getSearchDataFunc">Callback used to get the data for the search string.</param>
        /// <typeparam name="TData">The search data type.</typeparam>
        public void AddSearchDataCallback<TData>(Func<TData, IEnumerable<string>> getSearchDataFunc)
        {
            m_SearchEngine.AddSearchDataCallback(getSearchDataFunc);
        }
        
        /// <summary>
        /// Adds a filter based on a binding path. The given token will resolve to a property at the specified <paramref name="path"/>.
        /// </summary>
        /// <param name="token">The identifier of the filter. Typically what precedes the operator in a filter.</param>
        /// <param name="path">The property this token should resolve to.</param>
        /// <param name="supportedOperatorTypes">List of supported operator tokens. Null for all operators.</param>
        public void AddSearchFilterProperty(string token, PropertyPath path, string[] supportedOperatorTypes = null)
        {
            m_SearchEngine.AddSearchFilterProperty(token, path, supportedOperatorTypes);
        }

        /// <summary>
        /// Adds a search filter based on a callback function. The given token will resolve to the result of the specified <paramref name="getSearchDataFunc"/>.
        /// </summary>
        /// <param name="token">The identifier of the filter. Typically what precedes the operator in a filter.</param>
        /// <param name="getSearchDataFunc">Callback used to get the object that is used in the filter. Takes an object of type TData and returns an object of type TFilter.</param>
        /// <param name="supportedOperatorTypes">List of supported operator tokens. Null for all operators.</param>
        /// <typeparam name="TData">The data type being searched.</typeparam>
        /// <typeparam name="TFilter">The return type for the filter.</typeparam>
        public void AddSearchFilterCallback<TData, TFilter>(string token, Func<TData, TFilter> getSearchDataFunc, string[] supportedOperatorTypes = null)
        {
            m_SearchEngine.AddSearchFilterCallback(token, getSearchDataFunc, supportedOperatorTypes);
        }

        /// <summary>
        /// Adds a filter to the filter popup menu. This can be used for discoverability and quick way to add filter text.
        /// </summary>
        /// <param name="token">The token for the filter. This should NOT include the operator.</param>
        /// <param name="filterText">The text or name to display to the user.</param>
        /// <param name="filterTooltip">An optional tooltip.</param>
        public void AddSearchFilterPopupItem(string token, string filterText, string filterTooltip = "")
        {
            m_FilterPopupElementItems.Add(new FilterPopupElementChoice
            {
                Token = token,
                Text =  filterText,
                Tooltip = filterTooltip
            });
            
            UpdateControls();
        }
        
        /// <summary>
        /// Executes the search using the specified search string. 
        /// </summary>
        /// <remarks>
        /// This method can be used when the underlying data changes and the search must be explicitly run.
        /// </remarks>
        public void Search(string searchString)
        {
            SetValueWithoutNotify(searchString);
            
            m_DelayedSearch?.Pause();
            m_DelayedSearch = null;
            
            foreach (var target in m_SearchTargets)
                target.Parse(m_SearchStringTextField.text);
        }

        /// <summary>
        /// Executes the search using the current search string. 
        /// </summary>
        /// <remarks>
        /// This method can be used when the underlying data changes and the search must be explicitly run.
        /// </remarks>
        public void Search()
        {
            m_DelayedSearch?.Pause();
            m_DelayedSearch = null;
            
            foreach (var target in m_SearchTargets)
                target.Parse(m_SearchStringTextField.text);
        }

        /// <summary>
        /// Executes the search after a specified delay.
        /// </summary>
        /// <param name="delayMs">The number of milliseconds to wait before executing the search.</param>
        void SearchDelayed(long delayMs)
        {
            if (delayMs <= 0)
            {
                Search();
            }
            else
            {
                m_DelayedSearch?.Pause();
                m_DelayedSearch = schedule.Execute(Search).StartingIn(delayMs);
            }
        }
        
        /// <summary>
        /// Registers a high level search handler based on the specified bindings. The collection at <paramref name="sourceDataPath"/> will be read from, filtered and written to the <paramref name="filterDataPath"/>.
        /// </summary>
        /// <remarks>
        /// After the initial setup it is recommended to invoke <see cref="Search"/> to initialize the filtered data.
        /// </remarks>
        /// <param name="propertyElement">The property element to use for the data.</param>
        /// <param name="sourceDataPath">The source data path to read from.</param>
        /// <param name="filterDataPath">The filter data path to write to.</param>
        /// <returns>The search handler which can be used to customize the search or unregister the bindings.</returns>
        public ISearchHandler RegisterSearchQueryHandler(PropertyElement propertyElement, PropertyPath sourceDataPath, PropertyPath filterDataPath)
        {
            if (null != m_UxmlSearchHandlerBinding?.SearchHandler)
            {
                if (m_UxmlSearchHandlerBinding?.FilteredDataPath?.Equals(filterDataPath) ?? false) 
                    throw new InvalidOperationException($"SearchElement has invalid data bindings. The specified FilterDataPath=[{filterDataPath}] is already being written to by another search handler.");
            }
            
            var sourceDataBindingVisitor = new SourceDataBindingVisitor
            {
                SearchElement = this,
                PropertyElement = propertyElement, 
                SourceDataPath = sourceDataPath,
                SearchHandler = null
            };

            try
            {
                propertyElement.VisitAtPath(sourceDataPath, sourceDataBindingVisitor);
            }
            catch (InvalidPathException e)
            {
                throw new InvalidBindingException($"SearchElement has invalid data bindings. Invalid path SourceDataPath=[{sourceDataPath}]", e);
            }

            if (null == sourceDataBindingVisitor.SearchHandler)
                return null;

            var filterDataBindingVisitor = new FilterDataBindingVisitor
            {
                PropertyElement = propertyElement, 
                SourceDataPath = sourceDataPath,
                FilterDataPath = filterDataPath,
                SearchHandler = sourceDataBindingVisitor.SearchHandler
            };

            try
            {
                propertyElement.VisitAtPath(filterDataPath, filterDataBindingVisitor);
            }
            catch (InvalidPathException e)
            {
                if (null != sourceDataBindingVisitor.SearchHandler)
                    UnregisterSearchQueryHandler(sourceDataBindingVisitor.SearchHandler);

                throw new InvalidBindingException($"SearchElement has invalid data bindings. Invalid path FilterDataPath=[{filterDataPath}]", e);
            }
            catch (Exception)
            {
                if (null != sourceDataBindingVisitor.SearchHandler)
                    UnregisterSearchQueryHandler(sourceDataBindingVisitor.SearchHandler);
                
                throw;
            }
            
            return filterDataBindingVisitor.SearchHandler;
        }

        /// <summary>
        /// Adds a search query handler. The <see cref="ISearchQueryHandler{TData}.HandleSearchQuery"/> method will be invoked on the specified <see cref="ISearchHandler{TData}"/> whenever a search is performed.
        /// </summary>
        /// <param name="searchQueryHandler">The search handler to add.</param>
        /// <typeparam name="TData">The search data type.</typeparam>
        /// <exception cref="Exception">The specified search handler has already been registered.</exception>
        public void RegisterSearchQueryHandler<TData>(ISearchQueryHandler<TData> searchQueryHandler)
        {
            foreach (var target in m_SearchTargets)
            {
                if (target is SearchTarget<TData> typed && (typed.SearchQueryHandler == searchQueryHandler || typed.SearchQueryCallback == searchQueryHandler.HandleSearchQuery))
                    throw new InvalidOperationException("The given searchQueryHandler has already been registered.");
            }
            
            m_SearchTargets.Add(new SearchTarget<TData>(this, searchQueryHandler));
        }

        /// <summary>
        /// Adds a search query handler to the element. The specified callback will be invoked whenever a search is performed.
        /// </summary>
        /// <param name="searchQueryCallback">The callback to add.</param>
        /// <typeparam name="TData">The search data type.</typeparam>
        /// <exception cref="Exception">The callback has already been registered.</exception>
        public void RegisterSearchQueryHandler<TData>(Action<ISearchQuery<TData>> searchQueryCallback)
        {
            foreach (var target in m_SearchTargets)
            {
                if (target is SearchTarget<TData> typed && typed.SearchQueryCallback == searchQueryCallback)
                    throw new InvalidOperationException("The given searchQueryCallback has already been registered.");
            }

            m_SearchTargets.Add(new SearchTarget<TData>(this, searchQueryCallback));
        }

        /// <summary>
        /// Removes an untyped search handler from the element.
        /// </summary>
        /// <param name="searchHandler">The search handler to remove.</param>
        /// <exception cref="Exception">The specified search handler has not been registered.</exception>
        void UnregisterSearchQueryHandler(ISearchHandler searchHandler)
        {
            for (var i = 0; i < m_SearchTargets.Count; i++)
            {
                if (m_SearchTargets[i].GetSearchHandler() == searchHandler)
                {
                    m_SearchTargets.RemoveAt(i);
                    return;
                }
            }
        }

        /// <summary>
        /// Removes a search query handler from the element.
        /// </summary>
        /// <param name="searchQueryHandler">The search handler to remove.</param>
        /// <typeparam name="TData">The search data type.</typeparam>
        /// <exception cref="Exception">The specified search handler has not been registered.</exception>
        public void UnregisterSearchQueryHandler<TData>(ISearchQueryHandler<TData> searchQueryHandler)
        {
            for (var i = 0; i < m_SearchTargets.Count; i++)
            {
                if (m_SearchTargets[i] is SearchTarget<TData> typed && (typed.SearchQueryHandler == searchQueryHandler || typed.SearchQueryCallback == searchQueryHandler.HandleSearchQuery))
                {
                    m_SearchTargets.RemoveAt(i);
                    return;
                }
            }
        }

        /// <summary>
        /// Removes a search query callback from the element.
        /// </summary>
        /// <param name="searchQueryCallback">The callback to remove.</param>
        /// <typeparam name="TData">The search data type.</typeparam>
        /// <exception cref="Exception">The callback has not been registered.</exception>
        public void UnregisterSearchQueryHandler<TData>(Action<ISearchQuery<TData>> searchQueryCallback)
        {
            for (var i = 0; i < m_SearchTargets.Count; i++)
            {
                if (m_SearchTargets[i] is SearchTarget<TData> typed && typed.SearchQueryCallback == searchQueryCallback)
                {
                    m_SearchTargets.RemoveAt(i);
                    return;
                }
            }
        }

        /// <summary>
        /// Clears the search string and immediately invokes the search callback.
        /// </summary>
        public void ClearSearchString()
        {
            Search(string.Empty);
            UpdateControls();
        }

        void FocusSearchString()
        {
            m_SearchStringTextField.SelectRangeDelayed(m_SearchStringTextField.text.Length, m_SearchStringTextField.text.Length);
        }

        void UpdateControls()
        {
            var isSearchStringNullOrEmpty = string.IsNullOrEmpty(m_SearchStringTextField.text);
            
            SetCancelButtonEnabled(!isSearchStringNullOrEmpty);
            SetAddFilterButtonEnabled(isSearchStringNullOrEmpty && BackendType == SearchBackendType.QuickSearch && m_FilterPopupElementItems.Count > 0);
        }

        void SetCancelButtonEnabled(bool enabled)
        {
            SetControlEnabled(m_CancelButton, enabled);
        }

        void SetAddFilterButtonEnabled(bool enabled)
        {
            SetControlEnabled(m_AddFilterButton, enabled);
        }

        static void SetControlEnabled(VisualElement control, bool enabled)
        {
            if (enabled)
            {
                control.Show();
            }
            else
            {
                control.Hide();
            }
        }
    }
}