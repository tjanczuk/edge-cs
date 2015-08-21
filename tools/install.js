var fs = require('fs')
var path = require('path');
var childProcess = require('child_process');

function whereis() {
    var pathSep = process.platform === 'win32' ? ';' : ':';

    var directories = process.env.PATH.split(pathSep);

    for (var i = 0; i < directories.length; i++) {
    	for (var j = 0; j < arguments.length; j++) {
    		var filename = arguments[j];
	        var filePath = path.join(directories[i], filename);

	        if (fs.existsSync(filePath)) {
	            return filePath;
	        }
	    }
    }

    return null;
}

var dnuPath = whereis('dnu', 'dnu.cmd');

if (dnuPath) {
	childProcess.spawn(dnuPath, ['restore'], {
		cwd: path.join(__dirname, '..', 'lib', 'edge-cs-coreclr'),
		stdio: 'inherit'
	});
}
