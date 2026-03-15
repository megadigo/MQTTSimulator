using Cmf.Foundation.BusinessObjects;
using Cmf.Foundation.BusinessOrchestration.ConnectIoTManagement.InputObjects;
using Cmf.Foundation.BusinessOrchestration.GenericServiceManagement.InputObjects;
using IoTTestOrchestrator;
using System.Data;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace IPCCFXSimulator
{
    public static class Utilities
    {
        public static void WaitForConnection(TestScenario testRun, string managerName, int timeout = 60)
        {
            var automation = new GetFullAutomationStructureInput()
            {
                ManagerFilters = new Cmf.Foundation.BusinessObjects.QueryObject.FilterCollection()
                {
                    new Cmf.Foundation.BusinessObjects.QueryObject.Filter()
                    {
                        Name = "Name",
                        Value = managerName
                    }
                }
            }.GetFullAutomationStructureSync();

            (var controllers, var drivers) = ParseDriverInstancesFromDataSet(automation.NgpDataSet);

            testRun.Log.Info("Forcing Restart of the instances");

            new RestartAutomationControllerInstancesInput()
            {
                AutomationControllerInstances = controllers,
                IgnoreLastServiceId = true
            }.RestartAutomationControllerInstancesSync();

            testRun.Log.Info("Restarted the instances");

            foreach (long controllerInstanceId in controllers.Select(x => x.Id))
            {
                testRun.Utilities.WaitFor(timeout, $"controller for '{managerName}' never connected", () =>
                {
                    var entityInstance = new GetObjectByIdInput()
                    {
                        Id = controllerInstanceId,
                        Type = typeof(Cmf.Foundation.BusinessObjects.AutomationControllerInstance)
                    }.GetObjectByIdSync().Instance as AutomationControllerInstance;

                    testRun.Log.Info("# {0}: controller systemstate={1}", managerName, entityInstance.SystemState.ToString());

                    AutomationDriverInstanceCollection automationDriverInstanceCollection = new AutomationDriverInstanceCollection();

                    foreach (var driverInstance in drivers.Where(x => x.AutomationControllerInstance.Id == controllerInstanceId).Select(x => x.Id))
                    {
                        var reloadedDriverInstance = new GetObjectByIdInput()
                        {
                            Id = driverInstance,
                            Type = typeof(Cmf.Foundation.BusinessObjects.AutomationDriverInstance)
                        }.GetObjectByIdSync().Instance as AutomationDriverInstance;

                        automationDriverInstanceCollection.Add(reloadedDriverInstance);
                    }

                    //    bool allConnected = automationDriverInstanceCollection.Any(driverInstance =>
                    //        driverInstance.SystemState == AutomationSystemState.Running && driverInstance.CommunicationState == AutomationCommunicationState.Communicating);

                    //    return (entityInstance.SystemState == AutomationSystemState.Running && allConnected);
                    //});
                    bool doesNotHaveAnyDisconnected = !automationDriverInstanceCollection.Any(driverInstance =>
        driverInstance.SystemState != AutomationSystemState.Running && driverInstance.CommunicationState != AutomationCommunicationState.Communicating);

                    return (entityInstance.SystemState == AutomationSystemState.Running && doesNotHaveAnyDisconnected);
                });
            }
        }

        private static (AutomationControllerInstanceCollection, AutomationDriverInstanceCollection) ParseDriverInstancesFromDataSet(NgpDataSet ngpDataSet)
        {
            AutomationDriverInstanceCollection drivers = new AutomationDriverInstanceCollection();
            AutomationControllerInstanceCollection controllers = new AutomationControllerInstanceCollection();
            DataSet ds = ToDataSet(ngpDataSet);
            DataTable table = ds.Tables[0];
            for (int i = 0; i < table.Rows.Count; i++)
            {
                AutomationDriverInstance driver = new AutomationDriverInstance();
                string typeOfRow = Convert.ToString(table.Rows[i]["EntityTypeName"]) ?? string.Empty;
                if (typeOfRow == "AutomationControllerInstance")
                {
                    AutomationControllerInstance controller = new AutomationControllerInstance();
                    controller.Id = Convert.ToInt64(table.Rows[i]["AutomationControllerInstanceId"]);
                    controller.Name = Convert.ToString(table.Rows[i]["Name"]) ?? string.Empty;
                    controller.SystemState = (AutomationSystemState)Convert.ToInt32(table.Rows[i]["SystemState"]);

                    controller.AutomationManager = new AutomationManager();
                    controller.AutomationManager.Id = Convert.ToInt64(table.Rows[i]["AutomationManagerId"]);
                    controller.AutomationManager.Name = Convert.ToString(table.Rows[i]["AutomationManagerName"]);

                    controller.AutomationController = new AutomationController();
                    controller.AutomationController.Id = Convert.ToInt64(table.Rows[i]["ControllerId"]);
                    controller.AutomationController.Name = Convert.ToString(table.Rows[i]["ControllerName"]);
                    controller.AutomationController.ObjectType = new EntityType();
                    controller.AutomationController.ObjectType.Id = Convert.ToInt64(table.Rows[i]["ControllerObjectType"]);
                    controllers.Add(controller);
                }
                else
                {
                    AutomationDriverInstance driverInstance = new AutomationDriverInstance();
                    driverInstance.Id = Convert.ToInt64(table.Rows[i]["AutomationDriverInstanceId"]);
                    driverInstance.Name = Convert.ToString(table.Rows[i]["Name"]);
                    driverInstance.ObjectId = Convert.ToInt64(table.Rows[i]["ObjectId"]);
                    driverInstance.SystemState = (AutomationSystemState)Convert.ToInt32(table.Rows[i]["SystemState"]);
                    driverInstance.CommunicationState = (AutomationCommunicationState)Convert.ToInt32(table.Rows[i]["CommunicationState"]);
                    driverInstance.AutomationControllerInstance = new AutomationControllerInstance();
                    driverInstance.AutomationControllerInstance.Id = Convert.ToInt64(table.Rows[i]["AutomationControllerInstanceId"]);
                    driverInstance.AutomationManager = new AutomationManager();
                    driverInstance.AutomationManager.Id = Convert.ToInt64(table.Rows[i]["AutomationManagerId"]);
                    drivers.Add(driverInstance);
                }
            }

            drivers.ForEach((d) =>
            {
                d.AutomationControllerInstance = controllers.Where(x => x.Id == d.AutomationControllerInstance.Id).First();
            });

            return (controllers, drivers);
        }

        /// <summary>
        /// Convert a NgpDataSet to a DataSet
        /// </summary>
        /// <param name="dsd">NgpDataSet to convert</param>
        /// <returns>Returns a DataSet with all information of the NgpDataSet</returns>
        public static DataSet ToDataSet(NgpDataSet dsd)
        {
            DataSet ds = new DataSet();

            //Insert schema
            TextReader a = new StringReader(dsd.XMLSchema);
            XmlReader readerS = new XmlTextReader(a);
            ds.ReadXmlSchema(readerS);
            XDocument xdS = XDocument.Parse(dsd.XMLSchema);

            //Insert data
            UTF8Encoding encoding = new UTF8Encoding();
            Byte[] byteArray = encoding.GetBytes(dsd.DataXML);
            MemoryStream stream = new MemoryStream(byteArray);

            XmlReader reader = new XmlTextReader(stream);
            ds.ReadXml(reader);
            XDocument xd = XDocument.Parse(dsd.DataXML);

            foreach (DataTable dt in ds.Tables)
            {
                var rs = from row in xd.Descendants(dt.TableName)
                         select row;

                int i = 0;
                foreach (var r in rs)
                {
                    DataRowState state = DataRowState.Added;
                    if (r.Attribute("RowState") != null)
                    {
                        state = (DataRowState)Enum.Parse(typeof(DataRowState), r.Attribute("RowState").Value);
                    }

                    DataRow dr = dt.Rows[i];
                    dr.AcceptChanges();

                    if (state == DataRowState.Deleted)
                    {
                        dr.Delete();
                    }
                    else if (state == DataRowState.Added)
                    {
                        dr.SetAdded();
                    }
                    else if (state == DataRowState.Modified)
                    {
                        dr.SetModified();
                    }

                    i++;
                }
            }

            return ds;
        }
    }
}
