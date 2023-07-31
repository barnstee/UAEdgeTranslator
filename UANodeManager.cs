
namespace Opc.Ua.Edge.Translator
{
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;
    using Opc.Ua;
    using Opc.Ua.Edge.Translator.Interfaces;
    using Opc.Ua.Edge.Translator.Models;
    using Opc.Ua.Export;
    using Opc.Ua.Server;
    using Serilog;
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Threading;
    using System.Threading.Tasks;
    using BrowseNames = Ua.BrowseNames;
    using ObjectIds = Ua.ObjectIds;
    using ObjectTypeIds = Ua.ObjectTypeIds;
    using ObjectTypes = Ua.ObjectTypes;

    public class UANodeManager : CustomNodeManager2
    {
        private long _lastUsedId = 0;

        private bool _shutdown = false;

        private Dictionary<string, BaseDataVariableState> _uaVariables = new();

        private Dictionary<string, IAsset> _assets = new();

        private Dictionary<string, List<AssetTag>> _tags = new();

        private uint _counter = 0;

        private UACloudLibraryClient _uacloudLibraryClient = new();
        private UACloudLibraryClient _orgCloudLibraryClient = new();
        private bool useOrgCloudLibrary = false;

        private readonly string _wotNodeset = Path.Combine(Directory.GetCurrentDirectory(), "Nodesets", "Opc.Ua.WoT.nodeset2.xml");
        private readonly bool _useWotNodeset = false;

        public UANodeManager(IServerInternal server, ApplicationConfiguration configuration)
        : base(server, configuration)
        {
            SystemContext.NodeIdFactory = this;

            // add our default namespace
            List<string> namespaceUris = new List<string>
            {
                "http://opcfoundation.org/UA/EdgeTranslator/"
            };

            if (!Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), "settings")))
            {
                Directory.CreateDirectory(Path.Combine(Directory.GetCurrentDirectory(), "settings"));
            }

            // log into UA Cloud Library and download available Nodeset files
            _uacloudLibraryClient.Login(Environment.GetEnvironmentVariable("UACLURL"), Environment.GetEnvironmentVariable("UACLUsername"), Environment.GetEnvironmentVariable("UACLPassword"));

            useOrgCloudLibrary = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ORGUACLURL"));
            if (useOrgCloudLibrary)
            {
                _orgCloudLibraryClient.Login(Environment.GetEnvironmentVariable("ORGUACLURL"), Environment.GetEnvironmentVariable("ORGUACLUsername"), Environment.GetEnvironmentVariable("ORGUACLPassword"));
            }
            
            _useWotNodeset = File.Exists(_wotNodeset);
            if (_useWotNodeset)
            {
                LoadNamespaceUrisFromStream(namespaceUris, _wotNodeset);
            }

            // add a seperate namespace for each asset from the WoT TD files
            IEnumerable<string> WoTFiles = Directory.EnumerateFiles(Path.Combine(Directory.GetCurrentDirectory(), "settings"), "*.jsonld");
            foreach (string file in WoTFiles)
            {
                try
                {
                    string contents = File.ReadAllText(file);

                    // check file type (WoT TD or DTDL)
                    if (contents.Contains("\"@context\": \"dtmi:dtdl:context;2\""))
                    {
                        // parse DTDL contents and convert to WoT
                        contents = WoT2DTDLMapper.DTDL2WoT(contents);
                    }

                    // parse WoT TD files contents
                    ThingDescription td = JsonConvert.DeserializeObject<ThingDescription>(contents);

                    namespaceUris.Add("http://opcfoundation.org/UA/" + td.Name + "/");

                    FetchOPCUACompanionSpecs(namespaceUris, td);
                }
                catch (Exception ex)
                {
                    // skip this file, but log an error
                    Log.Logger.Error(ex.Message, ex);
                }
            }

            NamespaceUris = namespaceUris;
        }

        private void FetchOPCUACompanionSpecs(List<string> namespaceUris, ThingDescription td)
        {
            // check if an OPC UA companion spec is mentioned in the WoT TD file
            foreach (Uri opcuaCompanionSpecUrl in td.Context)
            {
                // support local Nodesets
                if (!opcuaCompanionSpecUrl.IsAbsoluteUri || (!opcuaCompanionSpecUrl.AbsoluteUri.Contains("http://") && !opcuaCompanionSpecUrl.AbsoluteUri.Contains("https://")))
                {
                    string nodesetFile = string.Empty;
                    if (Path.IsPathFullyQualified(opcuaCompanionSpecUrl.OriginalString))
                    {
                        // absolute file path
                        nodesetFile = opcuaCompanionSpecUrl.OriginalString;
                    }
                    else
                    {
                        // relative file path
                        nodesetFile = Path.Combine(Directory.GetCurrentDirectory(), opcuaCompanionSpecUrl.OriginalString);
                    }

                    Log.Logger.Information("Loading nodeset from local file: " + nodesetFile);
                    LoadNamespaceUrisFromStream(namespaceUris, nodesetFile);
                }
                else
                {
                    if (_uacloudLibraryClient.DownloadNamespace(Environment.GetEnvironmentVariable("UACLURL"), opcuaCompanionSpecUrl.OriginalString))
                    {
                        Log.Logger.Information("Loaded nodeset from Cloud Library URL: " + opcuaCompanionSpecUrl);

                        foreach (string nodesetFile in _uacloudLibraryClient._nodeSetFilenames)
                        {
                            LoadNamespaceUrisFromStream(namespaceUris, nodesetFile);
                        }
                    }
                    else if (useOrgCloudLibrary && _orgCloudLibraryClient.DownloadNamespace(Environment.GetEnvironmentVariable("ORGUACLURL"),opcuaCompanionSpecUrl.OriginalString))
                    {
                        Log.Logger.Information("Loaded nodeset from Organization Cloud Library URL: " + opcuaCompanionSpecUrl);

                        foreach (var nodesetFile in _orgCloudLibraryClient._nodeSetFilenames)
                        {
                            LoadNamespaceUrisFromStream(namespaceUris, nodesetFile);
                        }
                    }
                    else
                    {
                        Log.Logger.Warning($"Could not load nodeset {opcuaCompanionSpecUrl.OriginalString}");
                    }
                }
            }

            string validationError = _uacloudLibraryClient.ValidateNamespacesAndModels(Environment.GetEnvironmentVariable("UACLURL"), true);
            if (!string.IsNullOrEmpty(validationError))
            {
                Log.Logger.Error(validationError);
            }

            if (useOrgCloudLibrary)
            {
                validationError = _orgCloudLibraryClient.ValidateNamespacesAndModels(Environment.GetEnvironmentVariable("ORGUACLURL"),
                        true);
                if (!string.IsNullOrEmpty(validationError))
                {
                    Log.Logger.Error(validationError);
                }
            }
        }

        private void LoadNamespaceUrisFromStream(List<string> namespaceUris, string nodesetFile)
        {
            using (FileStream stream = new(nodesetFile, FileMode.Open, FileAccess.Read))
            {
                UANodeSet nodeSet = UANodeSet.Read(stream);
                if ((nodeSet.NamespaceUris != null) && (nodeSet.NamespaceUris.Length > 0))
                {
                    foreach (string ns in nodeSet.NamespaceUris)
                    {
                        if (!namespaceUris.Contains(ns))
                        {
                            namespaceUris.Add(ns);
                        }
                    }
                }
            }
        }

        public override NodeId New(ISystemContext context, NodeState node)
        {
            // for new nodes we create, pick our default namespace
            return new NodeId(Utils.IncrementIdentifier(ref _lastUsedId), (ushort)Server.NamespaceUris.GetIndex("http://opcfoundation.org/UA/EdgeTranslator/"));
        }

        public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
        {
            lock (Lock)
            {
                IList<IReference> references = null;
                if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out references))
                {
                    externalReferences[ObjectIds.ObjectsFolder] = references = new List<IReference>();
                }
                
                AddAssetManagementNodes(references);

                if (!Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), "settings")))
                {
                    Directory.CreateDirectory(Path.Combine(Directory.GetCurrentDirectory(), "settings"));
                }

                IEnumerable<string> WoTFiles = Directory.EnumerateFiles(Path.Combine(Directory.GetCurrentDirectory(), "settings"), "*.jsonld");
                foreach (string file in WoTFiles)
                {
                    try
                    {
                        ParseAsset(file, out ThingDescription td);
                        AddOPCUACompanionSpecNodes(td);
                        AddAsset(references, td, out BaseObjectState assetFolder);
                        
                        // create nodes for each TD property
                        foreach (KeyValuePair<string, Property> property in td.Properties)
                        {
                            foreach (object form in property.Value.Forms)
                            {
                                if (td.Base.ToLower().StartsWith("modbus://"))
                                {
                                    AddModbusNodes(td, assetFolder, property, form);
                                }
                            }
                        }

                        AddPredefinedNode(SystemContext, assetFolder);

                        _ = Task.Factory.StartNew(UpdateNodeValues, td.Title + " [" + td.Name + "]", TaskCreationOptions.LongRunning);
                    }
                    catch (Exception ex)
                    {
                        // skip this file, but log an error
                        Log.Logger.Error(ex.Message, ex);
                    }
                }
                
                AddReverseReferences(externalReferences);
            }
        }

        private void AddModbusNodes(ThingDescription td, BaseObjectState assetFolder, KeyValuePair<string, Property> property, object form)
        {
            ModbusForm modbusForm = JsonConvert.DeserializeObject<ModbusForm>(form.ToString());

            var variableNode = (BaseDataVariableState)Find(ExpandedNodeId.ToNodeId(ParseExpandedNodeId(modbusForm.OpcUaVariableNode), Server.NamespaceUris));
            if (variableNode != null)
            {
                Log.Logger.Information($"Mapping to existing variable node {variableNode.BrowseName.ToString()}");
                _uaVariables.Add(property.Key, variableNode);
            }
            else
            {
                // create an OPC UA variable
                if (!string.IsNullOrEmpty(modbusForm.OpcUaType))
                {
                    string[] opcuaTypeParts = modbusForm.OpcUaType.Split(new char[] { '=', ';' });
                    if ((opcuaTypeParts.Length > 3) && (opcuaTypeParts[0] == "nsu") && (opcuaTypeParts[2] == "i"))
                    {
                        string namespaceURI = opcuaTypeParts[1];
                        uint nodeID = uint.Parse(opcuaTypeParts[3]);

                        if (NamespaceUris.Contains(namespaceURI))
                        {
							// TODO: Check if this variable is part of a complex type and we need to load the complex type first and then assign a part of it to the new variable.
							// This is not yet supported in the current OPC Foundation .Net Standard OPC UA stack.
							// Waiting for OPCFoundation.NetStandard.Opc.Ua.Server.ComplexTypes to become available!
						
                            _uaVariables.Add(property.Key, CreateVariable(assetFolder, property.Key, new ExpandedNodeId(new NodeId(nodeID), namespaceURI), assetFolder.NodeId.NamespaceIndex));
                        }
                        else
                        {
                            // default to float
                            _uaVariables.Add(property.Key, CreateVariable(assetFolder, property.Key, new ExpandedNodeId(DataTypes.Float), assetFolder.NodeId.NamespaceIndex));
                        }
                    }
                    else
                    {
                        // default to float
                        _uaVariables.Add(property.Key, CreateVariable(assetFolder, property.Key, new ExpandedNodeId(DataTypes.Float), assetFolder.NodeId.NamespaceIndex));
                    }
                }
                else
                {
                    // default to float
                    _uaVariables.Add(property.Key, CreateVariable(assetFolder, property.Key, new ExpandedNodeId(DataTypes.Float), assetFolder.NodeId.NamespaceIndex));
                }
            }

            // create an asset tag and add to our list
            AssetTag tag = new()
            {
                Name = property.Key,
                Address = modbusForm.Href,
                Type = modbusForm.ModbusType.ToString(),
                PollingInterval = (int)modbusForm.ModbusPollingTime,
                Entity = modbusForm.ModbusEntity.ToString(),
                MappedUAExpandedNodeID = NodeId.ToExpandedNodeId(_uaVariables[property.Key].NodeId, Server.NamespaceUris).ToString()
            };

            string assetName = td.Title + " [" + td.Name + "]";

            if (!_tags.ContainsKey(assetName))
            {
                _tags.Add(assetName, new List<AssetTag>());
            }

            _tags[assetName].Add(tag);
        }

        private void AddOPCUACompanionSpecNodes(ThingDescription td)
        {
            // we need as many passes as we have nodesetfiles to make sure all references can be resolved
            for (int i = 0; i < _uacloudLibraryClient._nodeSetFilenames.Count; i++)
            {
                foreach (string nodesetFile in _uacloudLibraryClient._nodeSetFilenames)
                {
                    using (Stream stream = new FileStream(nodesetFile, FileMode.Open))
                    {
                        UANodeSet nodeSet = UANodeSet.Read(stream);

                        NodeStateCollection predefinedNodes = new NodeStateCollection();
                        nodeSet.Import(SystemContext, predefinedNodes);

                        for (int j = 0; j < predefinedNodes.Count; j++)
                        {
                            try
                            {
                                AddPredefinedNode(SystemContext, predefinedNodes[j]);
                            }
                            catch (Exception)
                            {
                                // do nothing
                            }
                        }
                    }
                }
            }

            if (useOrgCloudLibrary)
            {
                for (int i = 0; i < _orgCloudLibraryClient._nodeSetFilenames.Count; i++)
                {
                    foreach (string nodesetFile in _orgCloudLibraryClient._nodeSetFilenames)
                    {
                        using (Stream stream = new FileStream(nodesetFile, FileMode.Open))
                        {
                            UANodeSet nodeSet = UANodeSet.Read(stream);

                            NodeStateCollection predefinedNodes = new NodeStateCollection();
                            nodeSet.Import(SystemContext, predefinedNodes);

                            for (int j = 0; j < predefinedNodes.Count; j++)
                            {
                                try
                                {
                                    AddPredefinedNode(SystemContext, predefinedNodes[j]);
                                }
                                catch (Exception)
                                {
                                    // do nothing
                                }
                            }
                        }
                    }
                }
            }

            foreach (var opcuaCompanionSpecUrl in td.Context)
            {
                // support local Nodesets
                if (!opcuaCompanionSpecUrl.IsAbsoluteUri || (!opcuaCompanionSpecUrl.AbsoluteUri.Contains("http://") && !opcuaCompanionSpecUrl.AbsoluteUri.Contains("https://")))
                {
                    string nodesetFile = string.Empty;
                    if (Path.IsPathFullyQualified(opcuaCompanionSpecUrl.OriginalString))
                    {
                        // absolute file path
                        nodesetFile = opcuaCompanionSpecUrl.OriginalString;
                    }
                    else
                    {
                        // relative file path
                        nodesetFile = Path.Combine(Directory.GetCurrentDirectory(), opcuaCompanionSpecUrl.OriginalString);
                    }
                    Log.Logger.Information("Adding node set from local nodeset file");
                    using (Stream stream = new FileStream(nodesetFile, FileMode.Open))
                    {
                        UANodeSet nodeSet = UANodeSet.Read(stream);

                        NodeStateCollection predefinedNodes = new NodeStateCollection();
                        nodeSet.Import(SystemContext, predefinedNodes);

                        for (int i = 0; i < predefinedNodes.Count; i++)
                        {
                            try
                            {
                                AddPredefinedNode(SystemContext, predefinedNodes[i]);
                            }
                            catch (Exception)
                            {
                                // do nothing
                            }
                        }
                    }
                }
            }


        }

        private void ParseAsset(string file, out ThingDescription td)
        {
            string contents = File.ReadAllText(file);

            // check file type (WoT TD or DTDL)
            if (contents.Contains("\"@context\": \"dtmi:dtdl:context;2\""))
            {
                // parse DTDL contents and convert to WoT
                contents = WoT2DTDLMapper.DTDL2WoT(contents);
            }

            // parse WoT TD file contents
            td = JsonConvert.DeserializeObject<ThingDescription>(contents);

            // generate DTDL content, convert back to WoT TD and compare to original
            string dtdlContent = WoT2DTDLMapper.WoT2DTDL(contents);
            string convertedWoTTDContent = WoT2DTDLMapper.DTDL2WoT(dtdlContent);
            //Debug.Assert(JObject.DeepEquals(JObject.Parse(convertedWoTTDContent), JObject.Parse(contents)));
        }

        private void AddAsset(IList<IReference> references, ThingDescription td, out BaseObjectState assetFolder)
        {
            // create a connection to the asset
            if (td.Base.ToLower().StartsWith("modbus://"))
            {
                string[] modbusAddress = td.Base.Split(':');
                if (modbusAddress.Length != 3)
                {
                    throw new Exception("Expected Modbus address in the format modbus://ipaddress:port!");
                }

                // check if we can reach the Modbus asset
                ModbusTCPClient client = new();
                client.Connect(modbusAddress[1].TrimStart('/'), int.Parse(modbusAddress[2]));

                _assets.Add(td.Title + " [" + td.Name + "]", client);
            }

            // create a top-level OPC UA folder for the asset if no parent or asset node id is given
            var objectNodeId = ParseExpandedNodeId(td.OpcUaObjectNode);
            ExpandedNodeId parentNodeId = null;
            ExpandedNodeId typeNodeId = null;
            
            // If Asset has defined a target node in the address space, link to that node, otherwise create a new object.
            if (objectNodeId != null)
            {
                Log.Logger.Information($"Set asset to node: ns={objectNodeId.NamespaceIndex}, i={objectNodeId.Identifier}.");
                assetFolder = (BaseObjectState)Find(ExpandedNodeId.ToNodeId(objectNodeId, Server.NamespaceUris));
                assetFolder.Description = new Opc.Ua.LocalizedText("en", td.Title + " [" + td.Name + "]");
            }
            else
            {
                parentNodeId = ParseExpandedNodeId(td.OpcUaParentNode);
                if (parentNodeId != null)
                {
                    Log.Logger.Information($"Set asset parent node: ns={parentNodeId.NamespaceIndex}, i={parentNodeId.Identifier}.");
                }
                typeNodeId = ParseExpandedNodeId(td.OpcUaObjectType);
                if (typeNodeId != null)
                {
                    Log.Logger.Information($"Set asset type definition: ns={typeNodeId.NamespaceIndex}, i={typeNodeId.Identifier}.");
                }
                assetFolder = CreateAssetObject(null, td.Title + " [" + td.Name + "]", (ushort)Server.NamespaceUris.GetIndex("http://opcfoundation.org/UA/" + td.Name + "/"), ExpandedNodeId.ToNodeId(typeNodeId, Server.NamespaceUris));
                assetFolder.AddReference(ReferenceTypes.Organizes, true, parentNodeId ?? ObjectIds.ObjectsFolder);
            }
            
            assetFolder.EventNotifier = EventNotifiers.SubscribeToEvents;
            AddRootNotifier(assetFolder);
        }

        private ExpandedNodeId? ParseExpandedNodeId(string nodeString)
        {
            if (!string.IsNullOrEmpty(nodeString))
            {
                string[] parentNodeDetails = nodeString.Split('=', ';');
                if (parentNodeDetails.Length > 3 && parentNodeDetails[0] == "nsu" && parentNodeDetails[2] == "i")
                {
                    string namespaceUri = parentNodeDetails[1];

                    if (!NamespaceUris.Contains(namespaceUri))
                    {
                        return null;
                    }

                    switch (parentNodeDetails[2])
                    {
                        case "i":
                            return new ExpandedNodeId(uint.Parse(parentNodeDetails[3]),
                                (ushort)Server.NamespaceUris.GetIndex(namespaceUri));
                        case "s":
                            return new ExpandedNodeId(parentNodeDetails[3],
                                (ushort)Server.NamespaceUris.GetIndex(namespaceUri));
                        default:
                            return null;
                    }
                }
            }

            return null;
        }

        private void AddAssetManagementNodes(IList<IReference> references)
        {
            // If the WoT Nodeset is modeled, use that instead of creating new objects for the asset management.
            if (_useWotNodeset)
            {
                NodeStateCollection predefinedNodes = new NodeStateCollection();
                predefinedNodes.LoadFromBinaryResource(SystemContext, "Nodesets/Opc.Ua.WoT.PredefinedNodes.uanodes", this.GetType().GetTypeInfo().Assembly, true);
                
                for (int j = 0; j < predefinedNodes.Count; j++)
                {
                    try
                    {
                        AddPredefinedNode(SystemContext, predefinedNodes[j]);
                    }
                    catch (Exception)
                    {
                        // do nothing
                    }
                }
                
                var assetManagementFolder = (BaseObjectState)Find(ExpandedNodeId.ToNodeId(new ExpandedNodeId(WoT.Objects.AssetManagement, WoT.Namespaces.WoT), Server.NamespaceUris));

                var configureAsset = (MethodState)assetManagementFolder.FindChild(SystemContext, new QualifiedName("ConfigureAsset", (ushort)Server.NamespaceUris.GetIndex(WoT.Namespaces.WoT)));
                configureAsset.OnCallMethod = ConfigureAsset;

                var deleteAsset = (MethodState)assetManagementFolder.FindChild(SystemContext, new QualifiedName("DeleteAsset", (ushort)Server.NamespaceUris.GetIndex(WoT.Namespaces.WoT)));
                deleteAsset.OnCallMethod = DeleteAsset;

                var getAssets = (MethodState)assetManagementFolder.FindChild(SystemContext, new QualifiedName("GetAssets", (ushort)Server.NamespaceUris.GetIndex(WoT.Namespaces.WoT)));
                getAssets.OnCallMethod = GetAssets;

                AddPredefinedNode(SystemContext, assetManagementFolder);
            }
            else
            {
                // create our top-level asset management folder
                var assetManagementFolder = CreateFolder(null, "AssetManagement", (ushort)Server.NamespaceUris.GetIndex("http://opcfoundation.org/UA/EdgeTranslator/"));
                assetManagementFolder.AddReference(ReferenceTypes.Organizes, true, ObjectIds.ObjectsFolder);
                references.Add(new NodeStateReference(ReferenceTypes.Organizes, false, assetManagementFolder.NodeId));
                assetManagementFolder.EventNotifier = EventNotifiers.SubscribeToEvents;
                AddRootNotifier(assetManagementFolder);

                // create our methods
                MethodState configureAssetMethod = CreateMethod(assetManagementFolder, "ConfigureAsset", (ushort)Server.NamespaceUris.GetIndex("http://opcfoundation.org/UA/EdgeTranslator/"));
                configureAssetMethod.OnCallMethod = new GenericMethodCalledEventHandler(ConfigureAsset);
                configureAssetMethod.InputArguments = CreateInputArguments(configureAssetMethod, "WoTThingDescription", "The WoT Thing Description of the asset to be configured", (ushort)Server.NamespaceUris.GetIndex("http://opcfoundation.org/UA/EdgeTranslator/"));

                MethodState deleteAssetMethod = CreateMethod(assetManagementFolder, "DeleteAsset", (ushort)Server.NamespaceUris.GetIndex("http://opcfoundation.org/UA/EdgeTranslator/"));
                deleteAssetMethod.OnCallMethod = new GenericMethodCalledEventHandler(DeleteAsset);
                deleteAssetMethod.InputArguments = CreateInputArguments(deleteAssetMethod, "AssetID", "The ID of the asset to be deleted", (ushort)Server.NamespaceUris.GetIndex("http://opcfoundation.org/UA/EdgeTranslator/"));

                MethodState getAssetsMethod = CreateMethod(assetManagementFolder, "GetAssets", (ushort)Server.NamespaceUris.GetIndex("http://opcfoundation.org/UA/EdgeTranslator/"));
                getAssetsMethod.OnCallMethod = new GenericMethodCalledEventHandler(GetAssets);
                getAssetsMethod.OutputArguments = CreateOutputArguments(getAssetsMethod, "AssetIDs", "The IDs of the assets currently defined", (ushort)Server.NamespaceUris.GetIndex("http://opcfoundation.org/UA/EdgeTranslator/"));
                AddPredefinedNode(SystemContext, assetManagementFolder);
            }
            
        }

        private PropertyState<Argument[]> CreateInputArguments(NodeState parent, string name, string description, ushort namespaceIndex)
        {
            PropertyState<Argument[]> arguments = new PropertyState<Argument[]>(parent)
            {
                NodeId = new NodeId(parent.BrowseName.Name + "InArgs", namespaceIndex),
                BrowseName = BrowseNames.InputArguments,
                TypeDefinitionId = VariableTypeIds.PropertyType,
                ReferenceTypeId = ReferenceTypeIds.HasProperty,
                DataType = DataTypeIds.Argument,
                ValueRank = ValueRanks.OneDimension,
                Value = new Argument[]
                {
                    new Argument { Name = name, Description = description, DataType = DataTypeIds.String, ValueRank = ValueRanks.Scalar }
                }
            };

            arguments.DisplayName = arguments.BrowseName.Name;

            return arguments;
        }

        private PropertyState<Argument[]> CreateOutputArguments(NodeState parent, string name, string description, ushort namespaceIndex) {
            PropertyState<Argument[]> arguments = new PropertyState<Argument[]>(parent) {
                NodeId = new NodeId(parent.BrowseName.Name + "OutArgs", namespaceIndex),
                BrowseName = BrowseNames.OutputArguments,
                TypeDefinitionId = VariableTypeIds.PropertyType,
                ReferenceTypeId = ReferenceTypeIds.HasProperty,
                DataType = DataTypeIds.Argument,
                ValueRank = ValueRanks.OneDimension,
                Value = new Argument[]
                {
                    new Argument { Name = name, Description = description, DataType = DataTypeIds.String, ValueRank = ValueRanks.Scalar }
                }
            };

            arguments.DisplayName = arguments.BrowseName.Name;

            return arguments;
        }

        private FolderState CreateFolder(NodeState parent, string name, ushort namespaceIndex)
        {
            FolderState folder = new FolderState(parent)
            {
                SymbolicName = name,
                ReferenceTypeId = ReferenceTypes.Organizes,
                TypeDefinitionId = ObjectTypeIds.FolderType,
                NodeId = new NodeId(name, namespaceIndex),
                BrowseName = new QualifiedName(name, namespaceIndex),
                DisplayName = new Ua.LocalizedText("en", name),
                WriteMask = AttributeWriteMask.None,
                UserWriteMask = AttributeWriteMask.None,
                EventNotifier = EventNotifiers.None
            };
            parent?.AddChild(folder);

            return folder;
        }

        private BaseObjectState CreateAssetObject(NodeState parent, string name, ushort namespaceIndex, NodeId typeDefinition = null)
        {
            BaseObjectState folder = new BaseObjectState(parent)
            {
                SymbolicName = name,
                ReferenceTypeId = ReferenceTypes.Organizes,
                TypeDefinitionId = typeDefinition ?? ObjectTypeIds.BaseObjectType,
                NodeId = new NodeId(name, namespaceIndex),
                BrowseName = new QualifiedName(name, namespaceIndex),
                DisplayName = new Ua.LocalizedText("en", name),
                WriteMask = AttributeWriteMask.None,
                UserWriteMask = AttributeWriteMask.None,
                EventNotifier = EventNotifiers.None
            };
            parent?.AddChild(folder);

            return folder;
        }

        private BaseDataVariableState CreateVariable(NodeState parent, string name, ExpandedNodeId type, ushort namespaceIndex)
        {
            BaseDataVariableState variable = new BaseDataVariableState(parent)
            {
                SymbolicName = name,
                ReferenceTypeId = ReferenceTypes.Organizes,
                NodeId = new NodeId(name, namespaceIndex),
                BrowseName = new QualifiedName(name, namespaceIndex),
                DisplayName = new Ua.LocalizedText("en", name),
                WriteMask = AttributeWriteMask.None,
                UserWriteMask = AttributeWriteMask.None,
                AccessLevel = AccessLevels.CurrentRead,
                DataType = ExpandedNodeId.ToNodeId(type, Server.NamespaceUris)
            };
            parent?.AddChild(variable);

            return variable;
        }

        private MethodState CreateMethod(NodeState parent, string name, ushort namespaceIndex)
        {
            MethodState method = new MethodState(parent)
            {
                SymbolicName = name,
                ReferenceTypeId = ReferenceTypeIds.HasComponent,
                NodeId = new NodeId(name, namespaceIndex),
                BrowseName = new QualifiedName(name, namespaceIndex),
                DisplayName = new Ua.LocalizedText("en", name),
                WriteMask = AttributeWriteMask.None,
                UserWriteMask = AttributeWriteMask.None,
                Executable = true,
                UserExecutable = true
            };

            parent?.AddChild(method);

            return method;
        }

        private ServiceResult ConfigureAsset(ISystemContext context, MethodState method, IList<object> inputArguments, IList<object> outputArguments)
        {
            if (inputArguments.Count == 0)
            {
                return new ServiceResult(StatusCodes.BadInvalidArgument);
            }

            try
            {
                // test if we can parse the content and connect to the asset
                string contents = inputArguments[0].ToString();

                // check file type (WoT TD or DTDL)
                if (contents.Contains("\"@context\": \"dtmi:dtdl:context;2\""))
                {
                    // parse DTDL contents and convert to WoT
                    contents = WoT2DTDLMapper.DTDL2WoT(contents);
                }

                // parse WoT TD file contents
                ThingDescription td = JsonConvert.DeserializeObject<ThingDescription>(contents);

                // create a connection to the asset
                if (td.Base.ToLower().StartsWith("modbus://"))
                {
                    string[] modbusAddress = td.Base.Split(':');
                    if (modbusAddress.Length != 3)
                    {
                        throw new Exception("Expected Modbus address in the format modbus://ipaddress:port!");
                    }

                    // check if we can reach the Modbus asset
                    ModbusTCPClient client = new();
                    client.Connect(modbusAddress[1].TrimStart('/'), int.Parse(modbusAddress[2]));
                    client.Disconnect();
                }

                var assetGuid = Guid.NewGuid();
                File.WriteAllText(Path.Combine(Directory.GetCurrentDirectory(), "settings", assetGuid + ".jsonld"), contents);

                outputArguments[0] = assetGuid;
                outputArguments[1] = StatusCodes.Good;

                _ = Task.Run(() => HandleServerRestart());

                return ServiceResult.Good;
            }
            catch (Exception ex)
            {
                Log.Logger.Error(ex.Message, ex);
                return new ServiceResult(ex);
            }
        }

        private void HandleServerRestart()
        {
            _shutdown = true;

            Program.App.Stop();
            Program.App.Start(new UAServer()).GetAwaiter().GetResult();
        }

        private ServiceResult GetAssets(ISystemContext context, MethodState method, IList<object> inputArguments, IList<object> outputArguments)
        {
            if (outputArguments.Count == 0)
            {
                return new ServiceResult(StatusCodes.BadInvalidArgument);
            }

            outputArguments[0] = string.Empty;
            foreach (string asset in _assets.Keys)
            {
                outputArguments[0] += asset + ",";
            }
            outputArguments[0] = ((string)outputArguments[0]).TrimEnd(',');

            return ServiceResult.Good;
        }

        private ServiceResult DeleteAsset(ISystemContext context, MethodState method, IList<object> inputArguments, IList<object> outputArguments)
        {
            if (inputArguments.Count == 0)
            {
                return new ServiceResult(StatusCodes.BadInvalidArgument);
            }

            IEnumerable<string> WoTFiles = Directory.EnumerateFiles(Path.Combine(Directory.GetCurrentDirectory(), "settings"), "*.jsonld");
            foreach (string file in WoTFiles)
            {
                try
                {
                    string contents = File.ReadAllText(file);

                    // check file type (WoT TD or DTDL)
                    if (contents.Contains("\"@context\": \"dtmi:dtdl:context;2\""))
                    {
                        // parse DTDL contents and convert to WoT
                        contents = WoT2DTDLMapper.DTDL2WoT(contents);
                    }

                    // parse WoT TD files contents
                    ThingDescription td = JsonConvert.DeserializeObject<ThingDescription>(contents);

                    if (inputArguments[0].ToString() == td.Title)
                    {
                        File.Delete(file);
                    }
                }
                catch (Exception ex)
                {
                    Log.Logger.Error(ex.Message, ex);
                    return new ServiceResult(ex);
                }
            }

            _ = Task.Run(() => HandleServerRestart());

            return ServiceResult.Good;
        }

        private void UpdateNodeValues(object assetNameObject)
        {
            while (!_shutdown)
            {
                Thread.Sleep(1000);

                _counter++;

                string assetName = (string) assetNameObject;
                if (string.IsNullOrEmpty(assetName) || !_tags.ContainsKey(assetName) || !_assets.ContainsKey(assetName))
                {
                    throw new Exception("Cannot find asset: " +  assetName);
                }

                foreach (AssetTag tag in _tags[assetName])
                {
                    try
                    {
                        if (_assets[assetName] is ModbusTCPClient)
                        {
                            if (_counter * 1000 % tag.PollingInterval == 0)
                            {
                                ModbusTCPClient.FunctionCode functionCode = ModbusTCPClient.FunctionCode.ReadCoilStatus;
                                if (tag.Entity == "Holdingregister")
                                {
                                    functionCode = ModbusTCPClient.FunctionCode.ReadHoldingRegisters;
                                }

                                string[] addressParts = tag.Address.Split(new char[] { '?', '&', '=' });

                                if ((addressParts.Length > 4) && (addressParts[1] == "address") && (addressParts[3] == "quantity"))
                                {
                                    // read tag
                                    byte unitID = byte.Parse(addressParts[0].TrimStart('/'));
                                    uint address = uint.Parse(addressParts[2]);
                                    ushort quantity = ushort.Parse(addressParts[4]);

                                    byte[] tagBytes = null;
                                    try
                                    {
                                        tagBytes = _assets[assetName].Read(unitID, functionCode.ToString(), address, quantity).GetAwaiter().GetResult();
                                    }
                                    catch (Exception ex)
                                    {
                                        Log.Logger.Error(ex.Message, ex);

                                        // try reconnecting
                                        string[] remoteEndpoint = _assets[assetName].GetRemoteEndpoint().Split(':');
                                        _assets[assetName].Disconnect();
                                        _assets[assetName].Connect(remoteEndpoint[0], int.Parse(remoteEndpoint[1]));
                                    }

                                    if ((tagBytes != null) && (tag.Type == "Float"))
                                    {
                                        _uaVariables[tag.Name].Value = BitConverter.ToSingle(ByteSwapper.Swap(tagBytes));
                                        _uaVariables[tag.Name].Timestamp = DateTime.UtcNow;
                                        _uaVariables[tag.Name].ClearChangeMasks(SystemContext, false);
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // skip this tag, but log an error
                        Log.Logger.Error(ex.Message, ex);
                    }
                }
            }
        }
    }
}
