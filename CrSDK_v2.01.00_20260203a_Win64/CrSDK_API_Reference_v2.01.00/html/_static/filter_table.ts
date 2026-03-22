 //===============================================================
 //  フィルタテーブルの共通変数　設定要！
 //===============================================================
const gTableClass = 'filter-table';

class cColDataElem{
  constructor(){

  }
  setFilterListElem(elem:HTMLDivElement){
    this._tfList = elem;
  }
  getFilterListElem(){
    return this._tfList;
  }
  setFilterButtonElem(elem: SVGElement){
    this._tsBtn = elem;
  }
  getFilterButtonElem(){
    return this._tsBtn;
  }
  setDataAllElem(elem:HTMLInputElement){
    this._tfData_ALL = elem;
  }
  getDataAllElem(){
    return this._tfData_ALL;
  }
  setFormElem(elem:HTMLFormElement){
    this._form = elem;
  }
  getFormElem(){
    return this._form;
  }
  setInStrElem(elem:HTMLInputElement){
    this._tfInStr = elem;
  }
  getInStrElem(){
    return this._tfInStr;
  }
  _tfList : HTMLDivElement;
  _tsBtn : SVGElement;
  _tfData_ALL : HTMLInputElement; // 「すべて」のチェックボックス
  _form :HTMLFormElement; // フィルターリスト内の要素をまとめたHTML要素
  _tfInStr : HTMLInputElement;
}

class cFilterTable{
  constructor(index: number){
    this._index = index;
    this._allDataByCol = [];
  }
  setHTMLTableElement(htmlTableElement: HTMLTableElement){
    this._htmlTableElem = htmlTableElement;
  }
  initFilterButton(){
    var wTR     = this._htmlTableElem.rows;
    var isAddBtn:boolean = false;

    // ------------------------------------------------------------
    //   テーブルにフィルタボタンを付ける
    // ------------------------------------------------------------
    for(var i=0; i < wTR.length; i++){
      var wTD     = wTR[i].cells;
      for(var j=0; j < wTD.length; j++){  
        // --- 1行目のセルを対象とする ---
        if(i==0){ 
          // --- フィルタ対象はボタンの次の行から -----------------
          this._startRow = i + 1;

          var dataByCol = new cColDataElem();
          this._allDataByCol[j] = dataByCol;
   
          // --- ボタンを追加（画像はsvgを使用） ------------------
          var tfArea = document.createElement("div");
          tfArea.className = "tfArea";
          {
            var tfImg = document.createElementNS("http://www.w3.org/2000/svg","svg");
            tfImg.classList.add("tfImg");
            tfImg.id = `tsBtn_${j}`;
  
            const argCol = j;
            var ins = this;
            tfImg.addEventListener('click', function(){
              ins.filterCloseOpen(argCol);
            });
            var path = document.createElementNS("http://www.w3.org/2000/svg","path");
            path.setAttribute("d", "M0 0 L9 0 L6 4 L6 8 L3 8 L3 4Z");
            tfImg.appendChild(path);
            this._allDataByCol[j].setFilterButtonElem(tfImg);
  
            tfArea.appendChild(tfImg);
          }

          var tfList = document.createElement("div");
          tfList.className = "tfList";
          tfList.id = `tfList_${j}`;
          tfList.style.display = "none";
          const filterList = this.createFilterList(j);
          filterList.forEach(element => {
            tfList.appendChild(element);
          });
          tfArea.appendChild(tfList);
          wTD[j].appendChild(tfArea);
   
          // --- フィルタボタンになる列を保存 -----------------------
          dataByCol.setFilterListElem(tfList);

          isAddBtn = true;
        }
      }
   
      // --- ボタンを付けたら以降の行は無視する -------------------
      if(isAddBtn){
        break;
      }
   
    }
  }

  createFilterList(argCol: number){
    //==============================================================
    //  指定列のフィルタリスト作成
    //==============================================================
    
    var ret:any[] = [];
    var wTABLE    = this._htmlTableElem;
    var wTR       = wTABLE.rows;
    var wItem:string[]     = [];              // クリックされた列の値
    var wNotNum   = 0;               // 1 : 数字でない
    var wItemSave = {};              // フィルタに設定した値がキー

    // ------------------------------------------------------------
    //  クリックされた列の値を取得する
    // ------------------------------------------------------------
    for(var i=this._startRow; i < wTR.length; i++){
      var j = i - this._startRow;

      wItem[j] = wTR[i].cells[argCol].innerText.toString();

      if(wItem[j].match(/^[-]?[0-9,\.]+$/)){
      }else{
          wNotNum = 1;
      }

    }

    // ------------------------------------------------------------
    //  列の値でソートを実行
    // ------------------------------------------------------------
      if(wNotNum == 0){
        wItem.sort(sortNumA);           // 数値で昇順
      }else{
        wItem.sort(sortStrA);           // 文字で昇順
      }

    // ------------------------------------------------------------
    //  「すべて」のチェックボックス作成
    // ------------------------------------------------------------
    {
      const wItemId ='tfData_ALL_'+argCol;

      var tfMeisai = document.createElement("div") as HTMLDivElement;
      tfMeisai.classList.add("tfMeisai");

      var chkAll = document.createElement("input") as HTMLInputElement;
      chkAll.type = "checkbox";
      chkAll.id = wItemId;
      chkAll.checked = true;
      var ins = this;
      chkAll.addEventListener('click', function(){
        ins.filterAllSet(argCol);
      });
      this._allDataByCol[argCol].setDataAllElem(chkAll);

      var btnAll = document.createElement("label");
      btnAll.setAttribute("for", wItemId);
      btnAll.innerText = '(すべて)';

      tfMeisai.appendChild(chkAll);
      tfMeisai.appendChild(btnAll);

      ret.push(tfMeisai);
    }

    // ------------------------------------------------------------
    //  列の値でフィルタのチェックボックスを作成する
    //    チェックボックスはformで囲む
    // ------------------------------------------------------------
    var tfForm = document.createElement("form") as HTMLFormElement;
    this._allDataByCol[argCol].setFormElem(tfForm);
    tfForm.name = `tfForm_${argCol}`;

    for(var i=0; i < wItem.length; i++){

      const wVal = trim(wItem[i]);

      if(wVal in wItemSave){
        // ---値でチェックボックスが作成されている(重複) ----------
      }else{

        // ---チェックボックスの作成 ------------------------------
        const wItemId ='tfData_'+argCol+'_r'+i;
        var tfMeisai = document.createElement("div") as HTMLDivElement;
        tfMeisai.classList.add("tfMeisai");
        var chkBox = document.createElement("input") as HTMLInputElement;
        chkBox.type = "checkbox";
        chkBox.id = wItemId;
        chkBox.value = wVal;
        chkBox.checked = true;
        var ins = this;
        chkBox.addEventListener('click',function(){
          ins.filterClick(argCol);
        });

        var lbl = document.createElement("label") as HTMLLabelElement;
        lbl.setAttribute("for",wItemId);
        lbl.innerText = wVal=='' ? '(空白)' : wVal;

        tfMeisai.appendChild(chkBox);
        tfMeisai.appendChild(lbl);
        tfForm.appendChild(tfMeisai);

        // ---重複判定用にチェックボックスの値を保存 --------------
        wItemSave[wVal]='1';
      }
    }
    ret.push(tfForm);

    // ------------------------------------------------------------
    //  文字抽出のinputを作成
    // ------------------------------------------------------------
    var tfInStr = document.createElement("div") as HTMLDivElement;
    tfInStr.classList.add("tfInStr");
    var findInput = document.createElement("input") as HTMLInputElement;
    findInput.id = `tfInStr_${argCol}`;
    findInput.placeholder = "含む文字抽出";
    tfInStr.appendChild(findInput);
    this._allDataByCol[argCol].setInStrElem(findInput);
    ret.push(tfInStr);

    // ------------------------------------------------------------
    //  「OK」「Cancel」ボタンの作成
    // ------------------------------------------------------------
    var tfBtnArea = document.createElement("div") as HTMLDivElement;
    tfBtnArea.classList.add("tfBtnArea");

    var okBtn = document.createElement("input") as HTMLInputElement;
    okBtn.type = "button";
    okBtn.value = "OK";
    var ins = this;
    okBtn.addEventListener('click',function(){
      ins.filterGo();
    });

    var cancelBtn = document.createElement("input") as HTMLInputElement;
    cancelBtn.type = "button";
    cancelBtn.value = "Cancel";
    var ins = this;
    cancelBtn.addEventListener('click',function(){
      ins.filterCancel(argCol);
    });
    tfBtnArea.appendChild(okBtn);
    tfBtnArea.appendChild(cancelBtn);
    ret.push(tfBtnArea);

    // ------------------------------------------------------------
    //  作成したhtmlを返す
    // ------------------------------------------------------------
    return ret;
  }

  filterClose(){
    // --- フィルタリストを一旦すべて閉じる -----------------------
    for(const col of Object.keys(this._allDataByCol)){
      var tfList = this._allDataByCol[col].getFilterListElem();
      tfList.style.display = 'none';
    }
  }

  filterCloseOpen(argCol:number){
    //==============================================================
    //  フィルタを閉じて開く
    //==============================================================
    
    // --- フィルタリストを一旦すべて閉じる -----------------------
    this.filterClose();
    
    // --- 指定された列のフィルタリストを開く ---------------------
    var elem = this._allDataByCol[argCol]._tfList;
    elem.style.display = '';

    // --- フィルタ条件の保存（キャンセル時に復元するため） -----
    this.filterSave(argCol, 'save');
  }
  filterCancel(argCol){
    //==============================================================
    //  キャンセルボタン押下
    //==============================================================
    
    this.filterSave(argCol, 'load');    // フィルタ条件の復元
    this.filterClose();           // フィルタリストを閉じる    
  }

  filterGo(){
    //===============================================================
    //  フィルタの実行
    //===============================================================
    var wTABLE  = this._htmlTableElem;
    var wTR     = wTABLE.rows;
    
    // ------------------------------------------------------------
    //  全ての非表示を一旦クリア
    // ------------------------------------------------------------
    for(var i = 0; i < wTR.length; i++){
      if(wTR[i].getAttribute('cmanFilterNone') !== null){
        wTR[i].removeAttribute('cmanFilterNone');
      }
    }
    
    // ------------------------------------------------------------
    //  フィルタボタンのある列を繰り返す
    // ------------------------------------------------------------
    for(var wCol of Object.keys(this._allDataByCol)){
      let dataByCol:cColDataElem = this._allDataByCol[wCol];
      var wAll       = dataByCol.getDataAllElem();     // 「すべて」のチェックボックス
      var wItemSave  = {};
      var wFilterBtn =  dataByCol.getFilterButtonElem();
      var wFilterStr =  dataByCol.getInStrElem();
    
      var wForm      = dataByCol.getFormElem();
      // -----------------------------------------------------------
      //  チェックボックスの整備（「すべて」の整合性）
      // -----------------------------------------------------------
      for (var i = 0; i < wForm.elements.length; i++){
        var srcElem = wForm.elements[i];
        if(!srcElem.hasAttribute("type"))continue;
        var elem = srcElem as HTMLInputElement;
        if(elem.type == 'checkbox'){
          if (elem.checked) {
            wItemSave[elem.value] = 1;      // チェックされている値を保存
          }
        }
      }
    
      // -----------------------------------------------------------
      //  フィルタ（非表示）の設定
      // -----------------------------------------------------------
      if((wAll.checked)&&(trim(wFilterStr.value) == '')){
        wFilterBtn.style.backgroundColor = '';              // フィルタなし色
      }
      else{
        wFilterBtn.style.backgroundColor = '#ffff00';       // フィルタあり色
    
        for(var i=this._startRow; i < wTR.length; i++){
    
          var wVal = trim(wTR[i].cells[wCol].innerText.toString());
    
          // --- チェックボックス選択によるフィルタ ----------------
          if(!wAll.checked){
            if(wVal in wItemSave){
            }
            else{
              wTR[i].setAttribute('cmanFilterNone','');
            }
          }
    
          // --- 抽出文字によるフィルタ ----------------------------
          if(wFilterStr.value != ''){
            const reg = new RegExp(wFilterStr.value);
            if(wVal.match(reg)){
            }
            else{
              wTR[i].setAttribute('cmanFilterNone','');
            }
          }
        }
      }
    }
    
    this.filterClose();
  }

  filterClick(argCol){
    //==============================================================
    //  フィルタリストのチェックボックスクリック
    //    「すべて」のチェックボックスと整合性を合わせる
    //==============================================================
    var wForm   = this._allDataByCol[argCol].getFormElem();
    var wCntOn  = 0;
    var wCntOff = 0;
    var wAll    = this._allDataByCol[argCol].getDataAllElem();   // 「すべて」のチェックボックス
    
    // --- 各チェックボックスの状態を集計する ---------------------
    for (var i = 0; i < wForm.elements.length; i++){
      var srcElem = wForm.elements[i];
      if(!srcElem.hasAttribute("type"))continue;
      var elem = srcElem as HTMLInputElement;
      if(elem.type == 'checkbox'){
        if (elem.checked) { wCntOn++;  }
        else { wCntOff++; }
      }
    }
    
    // --- 各チェックボックス集計で「すべて」を整備する -----------
    if((wCntOn == 0)||(wCntOff == 0)){
      wAll.checked = true;             // 「すべて」をチェックする
      this.filterAllSet(argCol);           // 各フィルタのチェックする
    }else{
      wAll.checked = false;           // 「すべて」をチェックを外す
    }
  }

  filterAllSet(argCol){
    //==============================================================
    //  「すべて」のチェック状態に合わせて、各チェックをON/OFF
    //==============================================================
    var wChecked = false;
    var wForm    = this._allDataByCol[argCol].getFormElem();
    
    var tfData_ALLElem = this._allDataByCol[argCol].getDataAllElem();
    if(tfData_ALLElem.checked){
      wChecked = true;
    }
    
    for (var i = 0; i < wForm.elements.length; i++){
      var srcElem = wForm.elements[i];
      if(!srcElem.hasAttribute("type"))continue;
      var elem = srcElem as HTMLInputElement;
      if(elem.type == 'checkbox'){
        elem.checked = wChecked;
      }
    }
  }

  filterSave(argCol:number, argFunc:string){
    //==============================================================
    //  フィルタリストの保存または復元
    //==============================================================
    
    // ---「すべて」のチェックボックス値を保存 ------------------
    var wAllCheck = this._allDataByCol[argCol].getDataAllElem();
    if(argFunc == 'save'){
      this._listSaveStatus[wAllCheck.id] = wAllCheck.checked;
    }else{
      wAllCheck.checked =this._listSaveStatus[wAllCheck.id];
    }
    
    // --- 各チェックボックス値を保存 ---------------------------
    var wForm    = this._allDataByCol[argCol].getFormElem();
    for (var i = 0; i < wForm.elements.length; i++){
      var srcElem = wForm.elements[i];
      if(!srcElem.hasAttribute("type"))continue;
      var elem = srcElem as HTMLInputElement;
      if(elem.type == 'checkbox'){
        if(argFunc == 'save'){
          this._listSaveStatus[elem.id] = elem.checked;
        }else{
          elem.checked = this._listSaveStatus[elem.id];
        }
      }
    }
    
    // --- 含む文字の入力を保存 ---------------------------------
    var wStrInput = this._allDataByCol[argCol].getInStrElem();
    if(argFunc == 'save'){
      this._inputStr = wStrInput.value;
    }else{
      wStrInput.value = this._inputStr;
    }
  }
  
  

  _index : number;
  _htmlTableElem : HTMLTableElement;
  _allDataByCol : {[col: number]: cColDataElem} = {}; // 列ごとのデータ
  _startRow : number;
  _listSaveStatus : {[id: string]: boolean} = {}; // チェックボックスのチェック状態
  _inputStr: string = ""; // 検索文字列
}

var filterTables: cFilterTable[] = [];
 
 //===============================================================
 //  オンロードでテーブル初期設定関数をCALL
 //===============================================================
 window.addEventListener('load', function() {
  tFilterInit();
});

function tFilterInit(){
 //==============================================================
 //  テーブルの初期設定
 //==============================================================
  var elements = document.getElementsByClassName(gTableClass);
  var wTABLEs: HTMLTableElement[] = [];
  for(var i=0;i<elements.length;++i){
    wTABLEs.push(elements[i] as HTMLTableElement);
  }
  for(var t=0;t<wTABLEs.length; ++t){
    var ins = new cFilterTable(t);
    var wTABLE = wTABLEs[t];

    ins.setHTMLTableElement(wTABLE);
    ins.initFilterButton();

    filterTables.push(ins);
  }

}

function sortNumA(a, b) {
 //==============================================================
 //  数字のソート関数（昇順）
 //==============================================================
  a = parseInt(a.replace(/,/g, ''));
  b = parseInt(b.replace(/,/g, ''));
 
  return a - b;
}

function sortStrA(a, b){
 //==============================================================
 //  文字のソート関数（昇順）
 //==============================================================
  a = a.toString().toLowerCase();  // 英大文字小文字を区別しない
  b = b.toString().toLowerCase();
 
  if     (a < b){ return -1; }
  else if(a > b){ return  1; }
  return 0;
}

function trim(argStr){
 //==============================================================
 //  trim
 //==============================================================
  var rcStr = argStr;
  rcStr	= rcStr.replace(/^[ 　\r\n]+/g, '');
  rcStr	= rcStr.replace(/[ 　\r\n]+$/g, '');
  return rcStr;
}
