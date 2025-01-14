using JsonApiDotNetCore.AtomicOperations;
using JsonApiDotNetCore.AtomicOperations.Processors;
using JsonApiDotNetCore.Middleware;
using JsonApiDotNetCore.Queries;
using JsonApiDotNetCore.Queries.Parsing;
using JsonApiDotNetCore.Queries.QueryableBuilding;
using JsonApiDotNetCore.QueryStrings;
using JsonApiDotNetCore.Repositories;
using JsonApiDotNetCore.Resources;
using JsonApiDotNetCore.Serialization.JsonConverters;
using JsonApiDotNetCore.Serialization.Request;
using JsonApiDotNetCore.Serialization.Request.Adapters;
using JsonApiDotNetCore.Serialization.Response;
using JsonApiDotNetCore.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace JsonApiDotNetCore.Configuration;

/// <summary>
/// A utility class that builds a JsonApi application. It registers all required services and allows the user to override parts of the startup
/// configuration.
/// </summary>
internal sealed class JsonApiApplicationBuilder : IJsonApiApplicationBuilder, IDisposable
{
    private readonly JsonApiOptions _options = new();
    private readonly IServiceCollection _services;
    private readonly IMvcCoreBuilder _mvcBuilder;
    private readonly ResourceGraphBuilder _resourceGraphBuilder;
    private readonly ServiceDiscoveryFacade _serviceDiscoveryFacade;
    private readonly ServiceProvider _intermediateProvider;

    public Action<MvcOptions>? ConfigureMvcOptions { get; set; }

    public JsonApiApplicationBuilder(IServiceCollection services, IMvcCoreBuilder mvcBuilder)
    {
        ArgumentGuard.NotNull(services);
        ArgumentGuard.NotNull(mvcBuilder);

        _services = services;
        _mvcBuilder = mvcBuilder;
        _intermediateProvider = services.BuildServiceProvider();

        var loggerFactory = _intermediateProvider.GetRequiredService<ILoggerFactory>();

        _resourceGraphBuilder = new ResourceGraphBuilder(_options, loggerFactory);
        _serviceDiscoveryFacade = new ServiceDiscoveryFacade(_services, _resourceGraphBuilder, loggerFactory);
    }

    /// <summary>
    /// Executes the action provided by the user to configure <see cref="JsonApiOptions" />.
    /// </summary>
    public void ConfigureJsonApiOptions(Action<JsonApiOptions>? configureOptions)
    {
        configureOptions?.Invoke(_options);
    }

    /// <summary>
    /// Executes the action provided by the user to configure <see cref="ServiceDiscoveryFacade" />.
    /// </summary>
    public void ConfigureAutoDiscovery(Action<ServiceDiscoveryFacade>? configureAutoDiscovery)
    {
        configureAutoDiscovery?.Invoke(_serviceDiscoveryFacade);
    }

    /// <summary>
    /// Configures and builds the resource graph with resources from the provided sources and adds it to the DI container.
    /// </summary>
    public void ConfigureResourceGraph(ICollection<Type> dbContextTypes, Action<ResourceGraphBuilder>? configureResourceGraph)
    {
        ArgumentGuard.NotNull(dbContextTypes);

        _serviceDiscoveryFacade.DiscoverResources();

        foreach (Type dbContextType in dbContextTypes)
        {
            var dbContext = (DbContext)_intermediateProvider.GetRequiredService(dbContextType);
            _resourceGraphBuilder.Add(dbContext);
        }

        configureResourceGraph?.Invoke(_resourceGraphBuilder);

        IResourceGraph resourceGraph = _resourceGraphBuilder.Build();

        _options.SerializerOptions.Converters.Add(new ResourceObjectConverter(resourceGraph));

        _services.TryAddSingleton(resourceGraph);
    }

    /// <summary>
    /// Configures built-in ASP.NET MVC components. Most of this configuration can be adjusted for the developers' need.
    /// </summary>
    public void ConfigureMvc()
    {
        _mvcBuilder.AddMvcOptions(options =>
        {
            options.EnableEndpointRouting = true;
            options.Filters.AddService<IAsyncJsonApiExceptionFilter>();
            options.Filters.AddService<IAsyncQueryStringActionFilter>();
            options.Filters.AddService<IAsyncConvertEmptyActionResultFilter>();
            ConfigureMvcOptions?.Invoke(options);
        });

        if (_options.ValidateModelState)
        {
            _mvcBuilder.AddDataAnnotations();
            _services.Replace(new ServiceDescriptor(typeof(IModelMetadataProvider), typeof(JsonApiModelMetadataProvider), ServiceLifetime.Singleton));
        }
    }

    /// <summary>
    /// Discovers DI registrable services in the assemblies marked for discovery.
    /// </summary>
    public void DiscoverInjectables()
    {
        _serviceDiscoveryFacade.DiscoverInjectables();
    }

    /// <summary>
    /// Registers the remaining internals.
    /// </summary>
    public void ConfigureServiceContainer(ICollection<Type> dbContextTypes)
    {
        ArgumentGuard.NotNull(dbContextTypes);

        if (dbContextTypes.Any())
        {
            _services.TryAddScoped(typeof(DbContextResolver<>));

            foreach (Type dbContextType in dbContextTypes)
            {
                Type dbContextResolverClosedType = typeof(DbContextResolver<>).MakeGenericType(dbContextType);
                _services.TryAddScoped(typeof(IDbContextResolver), dbContextResolverClosedType);
            }

            _services.TryAddScoped<IOperationsTransactionFactory, EntityFrameworkCoreTransactionFactory>();
        }
        else
        {
            _services.TryAddScoped<IOperationsTransactionFactory, MissingTransactionFactory>();
        }

        AddResourceLayer();
        AddRepositoryLayer();
        AddServiceLayer();
        AddMiddlewareLayer();
        AddSerializationLayer();
        AddQueryStringLayer();
        AddOperationsLayer();

        _services.TryAddScoped(typeof(IResourceChangeTracker<>), typeof(ResourceChangeTracker<>));
        _services.TryAddScoped<IPaginationContext, PaginationContext>();
        _services.TryAddScoped<IEvaluatedIncludeCache, EvaluatedIncludeCache>();
        _services.TryAddScoped<ISparseFieldSetCache, SparseFieldSetCache>();
        _services.TryAddScoped<IQueryLayerComposer, QueryLayerComposer>();
        _services.TryAddScoped<IInverseNavigationResolver, InverseNavigationResolver>();
    }

    private void AddMiddlewareLayer()
    {
        _services.TryAddSingleton<IJsonApiOptions>(_options);
        _services.TryAddSingleton<IJsonApiApplicationBuilder>(this);
        _services.TryAddSingleton<IExceptionHandler, ExceptionHandler>();
        _services.TryAddScoped<IAsyncJsonApiExceptionFilter, AsyncJsonApiExceptionFilter>();
        _services.TryAddScoped<IAsyncQueryStringActionFilter, AsyncQueryStringActionFilter>();
        _services.TryAddScoped<IAsyncConvertEmptyActionResultFilter, AsyncConvertEmptyActionResultFilter>();
        _services.TryAddSingleton<IJsonApiInputFormatter, JsonApiInputFormatter>();
        _services.TryAddSingleton<IJsonApiOutputFormatter, JsonApiOutputFormatter>();
        _services.TryAddSingleton<IJsonApiRoutingConvention, JsonApiRoutingConvention>();
        _services.TryAddSingleton<IControllerResourceMapping>(provider => provider.GetRequiredService<IJsonApiRoutingConvention>());
        _services.TryAddSingleton<IHttpContextAccessor, HttpContextAccessor>();
        _services.TryAddScoped<IJsonApiRequest, JsonApiRequest>();
        _services.TryAddScoped<IJsonApiWriter, JsonApiWriter>();
        _services.TryAddScoped<IJsonApiReader, JsonApiReader>();
        _services.TryAddScoped<ITargetedFields, TargetedFields>();
    }

    private void AddResourceLayer()
    {
        RegisterImplementationForInterfaces(ServiceDiscoveryFacade.ResourceDefinitionUnboundInterfaces, typeof(JsonApiResourceDefinition<,>));

        _services.TryAddScoped<IResourceDefinitionAccessor, ResourceDefinitionAccessor>();
        _services.TryAddScoped<IResourceFactory, ResourceFactory>();
    }

    private void AddRepositoryLayer()
    {
        RegisterImplementationForInterfaces(ServiceDiscoveryFacade.RepositoryUnboundInterfaces, typeof(EntityFrameworkCoreRepository<,>));

        _services.TryAddScoped<IResourceRepositoryAccessor, ResourceRepositoryAccessor>();

        _services.TryAddTransient<IQueryableBuilder, QueryableBuilder>();
        _services.TryAddTransient<IIncludeClauseBuilder, IncludeClauseBuilder>();
        _services.TryAddTransient<IOrderClauseBuilder, OrderClauseBuilder>();
        _services.TryAddTransient<ISelectClauseBuilder, SelectClauseBuilder>();
        _services.TryAddTransient<ISkipTakeClauseBuilder, SkipTakeClauseBuilder>();
        _services.TryAddTransient<IWhereClauseBuilder, WhereClauseBuilder>();
    }

    private void AddServiceLayer()
    {
        RegisterImplementationForInterfaces(ServiceDiscoveryFacade.ServiceUnboundInterfaces, typeof(JsonApiResourceService<,>));
    }

    private void RegisterImplementationForInterfaces(HashSet<Type> unboundInterfaces, Type unboundImplementationType)
    {
        foreach (Type unboundInterface in unboundInterfaces)
        {
            _services.TryAddScoped(unboundInterface, unboundImplementationType);
        }
    }

    private void AddQueryStringLayer()
    {
        _services.TryAddTransient<IQueryStringParameterScopeParser, QueryStringParameterScopeParser>();
        _services.TryAddTransient<IIncludeParser, IncludeParser>();
        _services.TryAddTransient<IFilterParser, FilterParser>();
        _services.TryAddTransient<ISortParser, SortParser>();
        _services.TryAddTransient<ISparseFieldTypeParser, SparseFieldTypeParser>();
        _services.TryAddTransient<ISparseFieldSetParser, SparseFieldSetParser>();
        _services.TryAddTransient<IPaginationParser, PaginationParser>();

        _services.TryAddScoped<IIncludeQueryStringParameterReader, IncludeQueryStringParameterReader>();
        _services.TryAddScoped<IFilterQueryStringParameterReader, FilterQueryStringParameterReader>();
        _services.TryAddScoped<ISortQueryStringParameterReader, SortQueryStringParameterReader>();
        _services.TryAddScoped<ISparseFieldSetQueryStringParameterReader, SparseFieldSetQueryStringParameterReader>();
        _services.TryAddScoped<IPaginationQueryStringParameterReader, PaginationQueryStringParameterReader>();
        _services.TryAddScoped<IResourceDefinitionQueryableParameterReader, ResourceDefinitionQueryableParameterReader>();

        RegisterDependentService<IQueryStringParameterReader, IIncludeQueryStringParameterReader>();
        RegisterDependentService<IQueryStringParameterReader, IFilterQueryStringParameterReader>();
        RegisterDependentService<IQueryStringParameterReader, ISortQueryStringParameterReader>();
        RegisterDependentService<IQueryStringParameterReader, ISparseFieldSetQueryStringParameterReader>();
        RegisterDependentService<IQueryStringParameterReader, IPaginationQueryStringParameterReader>();
        RegisterDependentService<IQueryStringParameterReader, IResourceDefinitionQueryableParameterReader>();

        RegisterDependentService<IQueryConstraintProvider, IIncludeQueryStringParameterReader>();
        RegisterDependentService<IQueryConstraintProvider, IFilterQueryStringParameterReader>();
        RegisterDependentService<IQueryConstraintProvider, ISortQueryStringParameterReader>();
        RegisterDependentService<IQueryConstraintProvider, ISparseFieldSetQueryStringParameterReader>();
        RegisterDependentService<IQueryConstraintProvider, IPaginationQueryStringParameterReader>();
        RegisterDependentService<IQueryConstraintProvider, IResourceDefinitionQueryableParameterReader>();

        _services.TryAddScoped<IQueryStringReader, QueryStringReader>();
        _services.TryAddSingleton<IRequestQueryStringAccessor, RequestQueryStringAccessor>();
    }

    private void RegisterDependentService<TCollectionElement, TElementToAdd>()
        where TCollectionElement : class
        where TElementToAdd : TCollectionElement
    {
        _services.AddScoped<TCollectionElement>(provider => provider.GetRequiredService<TElementToAdd>());
    }

    private void AddSerializationLayer()
    {
        _services.TryAddScoped<IResourceIdentifierObjectAdapter, ResourceIdentifierObjectAdapter>();
        _services.TryAddScoped<IRelationshipDataAdapter, RelationshipDataAdapter>();
        _services.TryAddScoped<IResourceObjectAdapter, ResourceObjectAdapter>();
        _services.TryAddScoped<IResourceDataAdapter, ResourceDataAdapter>();
        _services.TryAddScoped<IAtomicReferenceAdapter, AtomicReferenceAdapter>();
        _services.TryAddScoped<IResourceDataInOperationsRequestAdapter, ResourceDataInOperationsRequestAdapter>();
        _services.TryAddScoped<IAtomicOperationObjectAdapter, AtomicOperationObjectAdapter>();
        _services.TryAddScoped<IDocumentInResourceOrRelationshipRequestAdapter, DocumentInResourceOrRelationshipRequestAdapter>();
        _services.TryAddScoped<IDocumentInOperationsRequestAdapter, DocumentInOperationsRequestAdapter>();
        _services.TryAddScoped<IDocumentAdapter, DocumentAdapter>();

        _services.TryAddScoped<ILinkBuilder, LinkBuilder>();
        _services.TryAddScoped<IResponseMeta, EmptyResponseMeta>();
        _services.TryAddScoped<IMetaBuilder, MetaBuilder>();
        _services.TryAddSingleton<IFingerprintGenerator, FingerprintGenerator>();
        _services.TryAddSingleton<IETagGenerator, ETagGenerator>();
        _services.TryAddScoped<IResponseModelAdapter, ResponseModelAdapter>();
    }

    private void AddOperationsLayer()
    {
        _services.TryAddScoped(typeof(ICreateProcessor<,>), typeof(CreateProcessor<,>));
        _services.TryAddScoped(typeof(IUpdateProcessor<,>), typeof(UpdateProcessor<,>));
        _services.TryAddScoped(typeof(IDeleteProcessor<,>), typeof(DeleteProcessor<,>));
        _services.TryAddScoped(typeof(IAddToRelationshipProcessor<,>), typeof(AddToRelationshipProcessor<,>));
        _services.TryAddScoped(typeof(ISetRelationshipProcessor<,>), typeof(SetRelationshipProcessor<,>));
        _services.TryAddScoped(typeof(IRemoveFromRelationshipProcessor<,>), typeof(RemoveFromRelationshipProcessor<,>));

        _services.TryAddScoped<IOperationsProcessor, OperationsProcessor>();
        _services.TryAddScoped<IOperationProcessorAccessor, OperationProcessorAccessor>();
        _services.TryAddScoped<ILocalIdTracker, LocalIdTracker>();
    }

    public void Dispose()
    {
        _intermediateProvider.Dispose();
    }
}
