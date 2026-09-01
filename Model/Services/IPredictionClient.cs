using System.Threading.Tasks;

namespace Model.Services
{
	// The three prediction calls the pipeline needs, named by what they do rather
	// than by how they get there.
	//
	// Two implementations ship with Model.dll:
	//
	//   PythonClient          starts python.exe on the machine running the code
	//   HttpPredictionClient  calls the ML service over HTTP
	//
	// DevicePipeline.RunAsync takes this interface, so the same one call works
	// either way. Before this existed it took PythonClient directly and could only
	// ever run Python locally, which is why DeviceClusterServiceApp had to
	// reimplement the prediction steps by hand instead of calling the pipeline.
	//
	// Each method returns the raw JSON body. Callers deserialise it themselves,
	// exactly as they did when the script output was read from stdout.
	public interface IPredictionClient
	{
		// Device type for a batch of device IDs.
		// Local: predict_equipment.py   Service: POST /predict/device-type
		Task<string> PredictDeviceTypeAsync(object request);

		// Section and cluster for a batch of records.
		// Local: predict_sectioncluster.py   Service: POST /predict/section-cluster
		Task<string> PredictSectionClusterAsync(object request);

		// Ranked cluster candidates for one device, used by manual correction.
		// Local: predict_sectioncluster.py (same script, top_clusters payload)
		// Service: POST /predict/top-clusters
		Task<string> PredictTopClustersAsync(object request);
	}
}
