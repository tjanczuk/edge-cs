var path = require('path');

exports.getCompiler = function () {
	return process.env.EDGE_CS_NATIVE || (process.env.EDGE_USE_CORECLR ? 'Edge.js.CSharp' : path.join(__dirname, 'edge-cs.dll'));
};

exports.getCompilerDependencyManifest = function() {
	return path.join(__dirname, 'bootstrap', 'bin', 'Release', 'netstandard1.5', 'bootstrap.deps.json');
}
