import './styles/css/styles.css';
import PDFRenderer from './components/PDFRenderer';
import IndexTable from './components/IndexTable/IndexTable';
import IndexPDFTemplate from './pdf/pdfTemplates/IndexPDFTemplate';

function App() {
  return (
    <div className='App' dir='rtl'>
      <header className='App-header'></header>
      <PDFRenderer
        componentToRender={IndexTable}
        pdfComponent={IndexPDFTemplate}
        pdfFileName='CompaniesAssesment.pdf'
        height={500}
        width={900}
      />
    </div>
  );
}

export default App;
