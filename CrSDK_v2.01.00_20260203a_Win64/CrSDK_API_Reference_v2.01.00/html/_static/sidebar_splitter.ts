window.addEventListener('load', onLoad);

function onLoad() {
    const splitters = document.getElementsByClassName("splitter");
    if(splitters.length==0)return;

    const splitter = splitters[0];
    console.log("hello,"+splitter);

    // const sidebars = document.getElementsByClassName("wy-nav-side");
    const sidebars = document.getElementsByClassName("wy-side-scroll");
    if(sidebars.length==0)return;
    const sidebar:HTMLElement = sidebars[0] as HTMLElement;
    
    splitter.addEventListener("mousedown", (event) => {

        document.addEventListener("mousemove", resize, false);
        document.addEventListener("mouseup", () => {
        document.removeEventListener("mousemove", resize, false);
        }, false);
    });

    function resize(e) {
        const size = `${e.x}px`;
        console.log(`size:${size}`)
        sidebar.style.width = size;
    }
}
